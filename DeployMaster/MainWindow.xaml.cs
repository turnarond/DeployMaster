using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using Ookii.Dialogs.Wpf;
using Telnet;

namespace DeployMaster
{
    /// <summary>
    /// 主窗口：支持批量 FTP 部署
    /// </summary>
    public partial class MainWindow : Window
    {
        public static MainWindow mainWindow;

        private ObservableCollection<UploadItem> _uploadItems;
        public MainWindow()
        {
            InitializeComponent();
            mainWindow = this;

            // 初始化上传列表
            _uploadItems = new ObservableCollection<UploadItem>();
            list_uploadedItems.ItemsSource = _uploadItems;
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

        private async Task UploadFileToAllDevices(
            string filePath,
            string user, string pass,
            string remoteBasePath,
            string[] ips,
            bool shouldReboot)
        {
            var successes = new List<string>();
            var failures = new List<string>();

            string fileName = Path.GetFileName(filePath);
            AppendLog($"📤 开始部署文件: {fileName}");

            foreach (string ip in ips)
            {
                string targetIp = ip.Trim();
                string uri = $"ftp://{targetIp}{(targetIp.Contains(":") ? "" : ":21")}{remoteBasePath}{fileName}";

                try
                {
                    Dispatcher.Invoke(() => AppendLog($"➡️ 上传到 {targetIp}..."));
                    string result = ConnectionManager.FtpUpload(uri, user, pass, filePath);
                    successes.Add(targetIp);
                    Dispatcher.Invoke(() => AppendLog($"✅ 成功: {targetIp}"));
                }
                catch (Exception ex)
                {
                    string msg = ex.Message.Length > 100 ? ex.Message.Substring(0, 100) + "..." : ex.Message;
                    failures.Add($"{targetIp}({msg})");
                    Dispatcher.Invoke(() => AppendLog($"❌ 失败: {targetIp} - {ex.Message}"));
                }

                await Task.Delay(100); // 避免太快
            }

            Dispatcher.Invoke(() =>
            {
                AppendLog($"📊 文件 '{fileName}' 部署完成：成功 {successes.Count} | 失败 {failures.Count}");
                if (failures.Any()) AppendLog($"❌ 失败列表: {string.Join(", ", failures)}");
            });
        }

        /// <summary>
        /// 开始批量部署
        /// </summary>

        private async void Btn_deploy_Click(object sender, RoutedEventArgs e)
        {
            string user = txt_user.Text;
            string pass = txt_pass.Password;
            bool shouldReboot = chk_rebootAfterDeploy.IsChecked == true;
            string remotePath = txt_remotePath.Text?.TrimEnd('/') + "/";

            var uploadItems = _uploadItems.ToList();
            if (uploadItems.Count == 0)
            {
                AppendLog("❌ 错误：请至少添加一个要上传的文件或文件夹！");
                return;
            }

            if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(pass))
            {
                AppendLog("❌ 错误：请输入用户名和密码！");
                return;
            }

            string[] ips = txt_ipList.Text
                .Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => !string.IsNullOrEmpty(x))
                .ToArray();

            if (ips.Length == 0)
            {
                AppendLog("❌ 错误：请至少输入一个目标 IP！");
                return;
            }

            // 禁用按钮
            Btn_deploy.IsEnabled = false;
            AppendLog($"🚀 开始部署 {uploadItems.Count} 个内容到 {ips.Length} 台设备...");

            await Task.Run(async () =>
            {
                var successes = new List<string>();
                var failures = new List<string>();

                // ✅ 外层：遍历每台设备（这才是合理的顺序！）
                foreach (string ip in ips)
                {
                    string targetIp = ip.Trim();
                    string ipWithPort = targetIp.Contains(":") ? targetIp : $"{targetIp}:21";
                    string baseUri = $"ftp://{ipWithPort}{remotePath}";

                    AppendLog($"➡️ 正在部署到设备：{targetIp}");

                    bool allSuccess = true;

                    // ✅ 内层：遍历每个上传项（文件/文件夹）
                    foreach (var item in uploadItems)
                    {
                        try
                        {
                            if (item.IsFolder)
                            {
                                // 上传整个文件夹
                                await UploadFolderToDevice(
                                    item.FullPath,
                                    baseUri,
                                    user, pass);
                            }
                            else
                            {
                                // 上传单个文件
                                string fileName = Path.GetFileName(item.FullPath);
                                string uri = baseUri + fileName;
                                ConnectionManager.FtpUpload(uri, user, pass, item.FullPath);
                            }

                            AppendLog($"✅ 上传成功: {item.DisplayName}");
                        }
                        catch (Exception ex)
                        {
                            string msg = ex.Message.Length > 100 ? ex.Message.Substring(0, 100) + "..." : ex.Message;
                            AppendLog($"❌ 上传失败: {item.DisplayName} -> {msg}");
                            allSuccess = false;
                        }

                        await Task.Delay(50); // 小延迟，避免太快
                    }

                    // ✅ 所有内容上传成功后，才执行重启
                    if (allSuccess)
                    {
                        successes.Add(targetIp);

                        if (shouldReboot)
                        {
                            try
                            {
                                await RebootDevice(targetIp, user, pass);
                                AppendLog($"🔄 已发送重启命令: {targetIp}");
                            }
                            catch (Exception ex)
                            {
                                AppendLog($"⚠️ 重启失败: {targetIp} - {ex.Message}");
                                // 重启失败不计入部署失败
                            }
                        }
                    }
                    else
                    {
                        failures.Add(targetIp);
                    }

                    await Task.Delay(100); // 设备间延迟
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

        private async Task RebootDevice(string ip, string user, string pass)
        {
            Dispatcher.Invoke(() => AppendLog($"🔄 正在通过 Telnet 重启 {ip} ..."));

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

        private async Task UploadFolderToDevice(string localFolderPath, string baseFtpUri, string user, string pass)
        {
            var root = new DirectoryInfo(localFolderPath);
            string folderName = root.Name;
            string remoteRoot = baseFtpUri + folderName + "/";

            // 确保远程根目录存在
            await Task.Run(() => ConnectionManager.FtpEnsureDirectory(remoteRoot, user, pass));

            // 递归上传
            await Task.Run(() => UploadDirectoryRecursive(root, remoteRoot, user, pass));
        }

        private void UploadDirectoryRecursive(DirectoryInfo localDir, string remoteBaseUri, string user, string pass)
        {
            foreach (FileInfo file in localDir.GetFiles())
            {
                string uri = remoteBaseUri + Uri.EscapeDataString(file.Name);
                try
                {
                    ConnectionManager.FtpUpload(uri, user, pass, file.FullName);
                }
                catch (Exception ex)
                {
                    AppendLog($"⚠️ 文件上传失败: {file.Name} -> {ex.Message}");
                }
            }

            foreach (DirectoryInfo subdir in localDir.GetDirectories())
            {
                string newRemotePath = remoteBaseUri + Uri.EscapeDataString(subdir.Name) + "/";
                try
                {
                    ConnectionManager.FtpEnsureDirectory(newRemotePath, user, pass);
                }
                catch (Exception ex)
                {
                    AppendLog($"⚠️ 目录创建失败: {subdir.Name} -> {ex.Message}");
                    continue; // 跳过该子目录
                }

                UploadDirectoryRecursive(subdir, newRemotePath, user, pass);
            }
        }

        private void Btn_AddFiles_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Ookii.Dialogs.Wpf.VistaOpenFileDialog();
            dialog.Multiselect = true;
            dialog.Filter = "所有项目|*.*";
            dialog.Title = "选择要上传的文件";

            if (dialog.ShowDialog() == true)
            {
                foreach (var file in dialog.FileNames)
                {
                    if (File.Exists(file))
                    {
                        AddUploadItem(file, isFolder: false);
                    }
                }
            }
            UpdateDragHintVisibility();
        }

        private void Btn_AddFolders_Click(object sender, RoutedEventArgs e)
        {
            MessageBoxResult addMore;
            do
            {
                var dialog = new VistaFolderBrowserDialog();
                dialog.Description = "选择要上传的文件夹（点击“取消”结束）";

                if (dialog.ShowDialog() == true && !string.IsNullOrEmpty(dialog.SelectedPath))
                {
                    AddUploadItem(dialog.SelectedPath, isFolder: true);
                }

                addMore = MessageBox.Show("是否继续添加另一个文件夹？", "添加文件夹", MessageBoxButton.YesNo, MessageBoxImage.Question);
            } while (addMore == MessageBoxResult.Yes);

            UpdateDragHintVisibility();
        }

        private void List_uploadedItems_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void List_uploadedItems_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
            {
                foreach (var path in paths)
                {
                    var isFolder = Directory.Exists(path);
                    var isFile = File.Exists(path);
                    if (isFile) AddUploadItem(path, false);
                    if (isFolder) AddUploadItem(path, true);
                }
                UpdateDragHintVisibility();
            }
        }
        private void List_uploadedItems_KeyUp(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Delete && list_uploadedItems.SelectedItem != null)
            {
                var items = list_uploadedItems.SelectedItems.Cast<UploadItem>().ToList();
                foreach (var item in items)
                {
                    ((ObservableCollection<UploadItem>)list_uploadedItems.ItemsSource).Remove(item);
                }
                UpdateDragHintVisibility();
            }
        }

        /// <summary>
        /// 添加上传项（避免重复）
        /// </summary>
        private void AddUploadItem(string path, bool isFolder)
        {
            if (string.IsNullOrEmpty(path)) return;

            // 防止重复添加
            if (_uploadItems.Any(x => x.FullPath == path)) return;

            _uploadItems.Add(new UploadItem
            {
                FullPath = path,
                IsFolder = isFolder
            });
        }

        private void UpdateDragHintVisibility()
        {
            txt_dragHint.Visibility = list_uploadedItems.Items.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void Btn_ClearList_Click(object sender, RoutedEventArgs e)
        {
            _uploadItems.Clear();
            UpdateDragHintVisibility();
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