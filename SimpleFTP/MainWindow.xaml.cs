using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Telnet;

namespace SimpleFTP
{
    /// <summary>
    /// 主窗口：支持批量 FTP 部署
    /// </summary>
    public partial class MainWindow : Window
    {
        public static MainWindow mainWindow;

        public MainWindow()
        {
            InitializeComponent();
            mainWindow = this;

            // 移除光标闪烁（干扰日志）
            // 你原来的 Timer_Tick 会加 "_"，我们去掉它
        }

        /// <summary>
        /// 添加日志（线程安全）
        /// </summary>
        public void AppendLog(string message)
        {
            Dispatcher.InvokeAsync(() =>
            {
                // 1. 追加文本
                txt_log.AppendText($"{DateTime.Now:HH:mm:ss} {message}\n");

                // 2. 清理旧日志（可选，防止太长）
                var lines = txt_log.Text.Split(new[] { '\n' }, StringSplitOptions.None);
                if (lines.Length > 1000)
                {
                    var recent = lines.Skip(lines.Length - 500).ToArray(); // 保留后 500 行
                    txt_log.Text = string.Join("\n", recent);
                }

                // 3. 使用 Dispatcher.InvokeAsync 延迟滚动
                // 等待 UI 更新布局后再执行 ScrollToEnd
                txt_log.Dispatcher.InvokeAsync(() =>
                {
                    txt_log.ScrollToEnd();
                }, DispatcherPriority.Background);
            }, DispatcherPriority.ApplicationIdle);
        }

        /// <summary>
        /// 选择本地文件
        /// </summary>
        private void Btn_fileBrowse_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog();
            if (dlg.ShowDialog() == true)
            {
                txt_fileToUpload.Text = dlg.FileName;
                AppendLog($"📁 文件已选择: {dlg.FileName}");
                Btn_deploy.IsEnabled = true;
            }
        }

        /// <summary>
        /// 开始批量部署
        /// </summary>
        private async void Btn_deploy_Click(object sender, RoutedEventArgs e)
        {
            string localFile = txt_fileToUpload.Text;
            string user = txt_user.Text;
            string pass = txt_pass.Password;
            // ✅ 判断是否需要重启
            bool shouldReboot = chk_rebootAfterDeploy.IsChecked == true;
            string remotePath = txt_remotePath.Text?.TrimEnd('/') + "/";

            // 获取 IP 列表
            string[] ips = txt_ipList.Text
                .Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => !string.IsNullOrEmpty(x))
                .ToArray();

            if (string.IsNullOrEmpty(localFile) || !File.Exists(localFile))
            {
                AppendLog("❌ 错误：请先选择有效的本地文件！");
                return;
            }

            if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(pass))
            {
                AppendLog("❌ 错误：请输入用户名和密码！");
                return;
            }

            if (ips.Length == 0)
            {
                AppendLog("❌ 错误：请至少输入一个目标 IP！");
                return;
            }

            // 禁用按钮防止重复点击
            Btn_deploy.IsEnabled = false;
            AppendLog($"🚀 开始部署 {Path.GetFileName(localFile)} 到 {ips.Length} 台设备...");

            string ipSummary = string.Join(", ", ips);
            // 使用 Task.Run 避免阻塞 UI
            await Task.Run(async () =>
            {
                var successes = new List<string>();
                var failures = new List<string>();

                foreach (string ip in ips)
                {
                    string targetIp = ip.Trim();
                    string fullUri = $"ftp://{targetIp}{(targetIp.Contains(":") ? "" : ":21")}{remotePath}";

                    Dispatcher.Invoke(() => AppendLog($"📤 正在上传到 {targetIp} ..."));

                    try
                    {
                        string result = ConnectionManager.FtpUpload(fullUri, user, pass, localFile);
                        successes.Add(targetIp);
                        Dispatcher.Invoke(() => AppendLog($"✅ 成功: {targetIp}"));

                        // ---2.如果勾选了重启，则执行 Telnet 重启-- -
                        if (shouldReboot)
                        {
                            Dispatcher.Invoke(() => AppendLog($"🔄 正在通过 Telnet 重启 {targetIp} ..."));

                            // ✅ 创建 TelnetManager 实例
                            var telnet = new TelnetClient();

                            // 可选：监听日志
                            telnet.OnLogReceived += log => Console.WriteLine($"[Telnet] {log}");

                            // 连接
                            bool success = await telnet.ConnectAsync(ip, 23, user, pass);

                            if (success)
                            {
                                // 发送 reboot
                                await telnet.SendLineAsync("reboot");
                                // 立即断开（设备会重启）
                                await telnet.DisconnectAsync();
                            }
                            else
                            {
                                Console.WriteLine("连接失败");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"{targetIp}({ex.Message.Substring(0, Math.Min(50, ex.Message.Length))}...)");
                        Dispatcher.Invoke(() => AppendLog($"❌ 失败: {targetIp}"));
                    }

                    Thread.Sleep(100);
                }

                // ✅ 最终汇总
                Dispatcher.Invoke(() =>
                {
                    AppendLog($"🎉 批量部署完成！");
                    AppendLog($"📊 成功: {successes.Count} | 失败: {failures.Count}");
                    if (failures.Any()) AppendLog($"❌ 失败列表: {string.Join(", ", failures)}");
                    Btn_deploy.IsEnabled = true;
                });
            });
        }

        /// <summary>
        /// 清除日志
        /// </summary>
        private void Btn_clear_Click(object sender, RoutedEventArgs e)
        {
            txt_log.Text = "";
            AppendLog("🗑️ 日志已清除");
        }

        // ========================
        // 可选：保留原有功能
        // ========================

        // 如果你还想保留“连接服务器浏览文件”功能
        // 可以保留 Btn_connect_Click 和下载功能
        // 或者注释掉它们

        /*
        private void Btn_connect_Click(object sender, RoutedEventArgs e)
        {
            Uri uri = ParseUrlForFTP(txt_server.Text, txt_port.Text);
            connProfile = new ConnectionProfile(uri, txt_user.Text, txt_pass.Password, txt_port.Text);
            connMan = new ConnectionManager();
            bool isConnected = connMan.ConnectToFTP(connProfile);

            if (isConnected)
            {
                AppendLog("✅ 已连接，远程文件列表：");
                StringBuilder sb = new StringBuilder();
                sb.AppendLine(string.Format("{0,-30} {1,-15}", "文件名", "大小"));
                foreach (var item in connMan.lines)
                {
                    sb.AppendLine(string.Format("{0,-30} {1,-15}", item.FileDisplayName, item.FileSize));
                }
                txt_remoteFileSystem.Text = sb.ToString();
            }
            else
            {
                AppendLog($"❌ 连接失败: {connMan.connResponse}");
            }
        }

        private void Btn_Download_Click(object sender, RoutedEventArgs e)
        {
            string fileName = txt_downloadFile.Text?.Trim();
            if (string.IsNullOrEmpty(fileName)) return;

            foreach (var line in connMan?.lines ?? new List<FtpListItem>())
            {
                if (line.FileName.Equals(fileName))
                {
                    AppendLog($"📥 开始下载 {fileName}...");
                    try
                    {
                        ConnectionManager.FtpDownload(
                            connProfile.ConnUri.ToString(),
                            connProfile.ConnUser,
                            connProfile.ConnPass,
                            fileName,
                            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                            line.FileSize);
                        AppendLog($"✅ 下载完成: {fileName}");
                    }
                    catch (Exception ex)
                    {
                        AppendLog($"❌ 下载失败: {ex.Message}");
                    }
                    break;
                }
            }
        }
        */
    }
}