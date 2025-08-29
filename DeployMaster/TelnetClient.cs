using System;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Telnet
{
    public class TelnetClient : IDisposable
    {
        private TcpClient _tcp;
        private readonly CancellationTokenSource _connectCts = new CancellationTokenSource();

        public bool IsConnected { get { return _tcp != null && _tcp.Connected; } }

        public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(10);
        public TimeSpan DataTransferTimeout { get; set; } = TimeSpan.FromSeconds(5);

        public string Host { get; private set; }
        public int Port { get; private set; }
        public string Login { get; private set; }
        public string Password { get; private set; }

        // 自定义登录逻辑
        public Func<string, string, Task<bool>> LoginProc { get; set; }
        // 自定义连接检查
        public Func<Task<bool>> ConnectionCheckProc { get; set; }

        // 实时日志回调
        public Action<string> OnLogReceived;

        public TelnetClient()
        {
            LoginProc = DefaultLoginAsync;
            ConnectionCheckProc = DefaultConnectionCheckAsync;
        }

        /// <summary>
        /// 连接并登录
        /// </summary>
        public async Task<bool> ConnectAsync(string host, int port = 23, string login = null, string password = null)
        {
            Host = host;
            Port = port;
            Login = login;
            Password = password;

            return await ReconnectAsync();
        }

        /// <summary>
        /// 重连
        /// </summary>
        public async Task<bool> ReconnectAsync()
        {
            await DisconnectAsync();

            if (string.IsNullOrEmpty(Host) || Port < 1 || Port > 65535)
                return false;

            try
            {
                _tcp = new TcpClient();
                _tcp.SendTimeout = (int)DataTransferTimeout.TotalMilliseconds;
                _tcp.ReceiveTimeout = (int)DataTransferTimeout.TotalMilliseconds;
                _tcp.NoDelay = true; // 禁用 Nagle

                // 异步连接 + 超时控制
                var connectTask = _tcp.ConnectAsync(Host, Port);

                // 使用 Task.WhenAny + Delay 模拟超时
                using (var cts = new CancellationTokenSource())
                {
                    var timeoutTask = Task.Delay(ConnectTimeout, cts.Token);
                    var completedTask = await Task.WhenAny(connectTask, timeoutTask);

                    if (completedTask == timeoutTask)
                    {
                        // 超时，取消连接
                        cts.Cancel();
                        await DisconnectAsync();
                        return false;
                    }
                }

                if (connectTask.IsFaulted)
                {
                    await DisconnectAsync();
                    return false;
                }

                // 登录
                if (!string.IsNullOrEmpty(Login) && !string.IsNullOrEmpty(Password))
                {
                    if (!await LoginProc(Login, Password))
                    {
                        await DisconnectAsync();
                        return false;
                    }
                }

                // 连接检查
                if (!await ConnectionCheckProc())
                {
                    await DisconnectAsync();
                    return false;
                }

                return true;
            }
            catch
            {
                await DisconnectAsync();
                return false;
            }
        }

        /// <summary>
        /// 断开连接
        /// </summary>
        public async Task DisconnectAsync()
        {
            if (_tcp == null) return;

            try
            {
                if (_tcp.Connected)
                {
                    _tcp.Client.Shutdown(SocketShutdown.Both);
                }
                _tcp.Close();
                _tcp = null;
            }
            catch { }
        }

        /// <summary>
        /// 发送字符串（不换行）
        /// </summary>
        public async Task<bool> SendAsync(string text)
        {
            if (!IsConnected) return false;

            try
            {
                var data = Encoding.ASCII.GetBytes(text);
                await _tcp.GetStream().WriteAsync(data, 0, data.Length);
                return true;
            }
            catch
            {
                await DisconnectAsync();
                return false;
            }
        }

        /// <summary>
        /// 发送命令（带换行）
        /// </summary>
        public async Task<bool> SendLineAsync(string line)
        {
            return await SendAsync(line + "\n");
        }

        /// <summary>
        /// 读取所有可用数据
        /// </summary>
        public async Task<string> ReadAsync()
        {
            if (!IsConnected) return "";

            var sb = new StringBuilder();
            var buffer = new byte[256];

            try
            {
                var stream = _tcp.GetStream();
                while (stream.DataAvailable)
                {
                    int read = await stream.ReadAsync(buffer, 0, buffer.Length);
                    if (read > 0)
                    {
                        string text = Encoding.ASCII.GetString(buffer, 0, read);
                        sb.Append(text);
                    }
                    else
                    {
                        break;
                    }
                }

                string result = sb.ToString();
                OnLogReceived?.Invoke(result);
                return result;
            }
            catch
            {
                await DisconnectAsync();
                return "";
            }
        }

        /// <summary>
        /// 等待任意关键词出现
        /// </summary>
        public async Task<string> ReadUntilAnyAsync(string[] keywords, int timeoutMs = 5000)
        {
            var cts = new CancellationTokenSource(timeoutMs);
            var sb = new StringBuilder();
            var buffer = new byte[256];

            try
            {
                while (!cts.IsCancellationRequested)
                {
                    if (_tcp != null && _tcp.Client != null && _tcp.Client.Available > 0)
                    {
                        int read = await _tcp.GetStream().ReadAsync(buffer, 0, buffer.Length);
                        if (read > 0)
                        {
                            string text = Encoding.ASCII.GetString(buffer, 0, read);
                            sb.Append(text);
                            OnLogReceived?.Invoke(text);

                            // 检查关键词
                            foreach (string keyword in keywords)
                            {
                                int index = sb.ToString().IndexOf(keyword, StringComparison.OrdinalIgnoreCase);
                                if (index != -1)
                                {
                                    int endIndex = index + keyword.Length;
                                    if (endIndex < sb.Length)
                                    {
                                        char nextChar = sb[endIndex];
                                        if (IsBoundaryChar(nextChar))
                                            return sb.ToString();
                                    }
                                }
                            }
                        }
                    }
                    else
                    {
                        await Task.Delay(50);
                    }
                }

                return sb.ToString();
            }
            catch (OperationCanceledException)
            {
                return sb.ToString();
            }
            catch
            {
                await DisconnectAsync();
                return sb.ToString();
            }
            finally
            {
                cts.Dispose();
            }
        }

        private static bool IsBoundaryChar(char c)
        {
            return char.IsWhiteSpace(c) ||
                   c == ':' || c == '>' || c == '$' || c == '#' ||
                   c == '\r' || c == '\n' || c == '\0';
        }

        // 默认登录流程
        private async Task<bool> DefaultLoginAsync(string login, string password)
        {
            try
            {
                string r1 = await ReadUntilAnyAsync(new[] { "login:", "Login:" }, 8000);
                if (r1.IndexOf("login", StringComparison.OrdinalIgnoreCase) == -1)
                    return false;

                await SendLineAsync(login);

                string r2 = await ReadUntilAnyAsync(new[] { "password:", "Password:" }, 5000);
                if (r2.IndexOf("password", StringComparison.OrdinalIgnoreCase) == -1)
                    return false;

                await SendLineAsync(password);

                string r3 = await ReadUntilAnyAsync(new[] { "$", "#", ">" }, 8000);
                return r3.Contains("$") || r3.Contains("#") || r3.Contains(">");
            }
            catch
            {
                return false;
            }
        }

        // 默认连接检查
        private async Task<bool> DefaultConnectionCheckAsync()
        {
            try
            {
                await SendLineAsync("\n");
                string prompt = (await ReadAsync()).Trim();
                return prompt.Contains("$") || prompt.Contains("#") || prompt.Contains(">");
            }
            catch
            {
                return false;
            }
        }

        public void Dispose()
        {
            _connectCts.Cancel();
            _connectCts.Dispose();
            _tcp?.Dispose();
        }
    }
}