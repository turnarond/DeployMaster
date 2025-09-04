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
        private Dictionary<string, ObservableCollection<FtpRemoteItem>> _cachedFileTrees = new Dictionary<string, ObservableCollection<FtpRemoteItem>>();
        private List<string> _targetIPs = new List<string>();
        public MainWindow()
        {
            InitializeComponent();
            mainWindow = this;

            // 初始化上传列表
            _uploadItems = new ObservableCollection<UploadItem>();
            list_uploadedItems.ItemsSource = _uploadItems;

            // 初始化 cmb_targetIPs 数据
            Txt_ipList_TextChanged(null, null); // 触发初始填充
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
                var delopy_successes = new List<string>();
                var deploy_failures = new List<string>();
                var reboot_successes = new List<string>();
                var reboot_failures = new List<string>();

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
                        delopy_successes.Add(targetIp);

                        if (shouldReboot)
                        {
                            try
                            {
                                await RebootDevice(targetIp, user, pass);
                                reboot_successes.Add(targetIp);
                                AppendLog($"🔄 已发送重启命令: {targetIp}");
                            }
                            catch (Exception ex)
                            {
                                reboot_failures.Add(targetIp);
                                AppendLog($"⚠️ 重启失败: {targetIp} - {ex.Message}");
                                // 重启失败不计入部署失败
                            }
                        }
                    }
                    else
                    {
                        deploy_failures.Add(targetIp);
                    }

                    await Task.Delay(100); // 设备间延迟
                }

                // ✅ 最终汇总
                Dispatcher.Invoke(() =>
                {
                    AppendLog($"🎉 批量部署完成！");
                    AppendLog($"📊 部署成功: {delopy_successes.Count} | 部署失败: {deploy_failures.Count}");
                    if (deploy_failures.Any()) AppendLog($"❌ 失败列表: {string.Join(", ", deploy_failures)}");
                    if (shouldReboot)
                    {
                        AppendLog($"📊 重启成功: {reboot_successes.Count} | 重启失败: {reboot_failures.Count}");
                        if (deploy_failures.Any()) AppendLog($"❌ 失败列表: {string.Join(", ", reboot_failures)}");
                    }
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

        private async void Btn_RefreshRemote_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string user = txt_user.Text;
                string pass = txt_pass.Password;
                string remotePath = txt_remotePath.Text?.TrimEnd('/') + "/";
                string[] ips = txt_ipList.Text
                    .Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.Trim())
                    .Where(x => !string.IsNullOrEmpty(x))
                    .ToArray();

                if (ips.Length == 0)
                {
                    AppendLog("❌ 请先输入至少一个目标 IP！");
                    return;
                }

                string selectedIp = cmb_targetIPs.SelectedItem?.ToString();
                if (string.IsNullOrEmpty(selectedIp))
                {
                    selectedIp = ips.FirstOrDefault();
                }
                string uri = $"ftp://{selectedIp}:21{remotePath}";

                AppendLog($"🔍 正在获取 {selectedIp} 的远端目录: {remotePath}");

                // 异步获取
                var items = await Task.Run(() => ConnectionManager.FtpList(uri, user, pass));

                // 构建树结构
                var root = new FtpRemoteItem
                {
                    Name = remotePath.Split('/').LastOrDefault() ?? "/",
                    FullName = uri,
                    IsDirectory = true
                };

                foreach (var item in items)
                {
                    root.Children.Add(new FtpRemoteItem
                    {
                        Name = item.Name,
                        FullName = item.FullName,
                        IsDirectory = item.IsDirectory,
                        Size = item.Size,
                        ModifiedDate = item.ModifiedDate
                    });
                }

                // 在 Btn_RefreshRemote_Click 中，刷新前设置标题
                group_remoteFiles.Header = $"📁 {remotePath}"; // 可加图标
                // 绑定到 UI
                tree_remoteFiles.ItemsSource = new ObservableCollection<FtpRemoteItem> { root };

                AppendLog($"✅ 获取远端文件成功，共 {root.Children.Count} 项");
            }
            catch (Exception ex)
            {
                AppendLog($"❌ 获取远端文件失败: {ex.Message}");
            }
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
                AppendLog($"⚠️ Telnet 连接失败: {ip}");
            }
        }

        private async Task UploadFolderToDevice(string localFolderPath, string baseFtpUri, string user, string pass)
        {
            var root = new DirectoryInfo(localFolderPath);
            string folderName = root.Name;
            string remoteRoot = baseFtpUri + folderName + "/";

            await Task.Run(() =>
            {
                // 确保远程根目录存在
                ConnectionManager.FtpEnsureDirectory(remoteRoot, user, pass);

                // 递归上传
                UploadDirectoryRecursive(root, remoteRoot, user, pass);
            });
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

        private void Cmb_targetIPs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            string selected = cmb_targetIPs.SelectedItem?.ToString();
            if (!string.IsNullOrEmpty(selected))
                AppendLog($"📌 已选择目标设备：{selected}");
        }

        // <summary>
        /// 当 IP 列表文本变化时，更新下拉框的选项
        /// </summary>
        private void Txt_ipList_TextChanged(object sender, TextChangedEventArgs e)
        {
            // 防止设计器中触发
            if (!IsInitialized) return;

            var ips = txt_ipList.Text
                .Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => !string.IsNullOrEmpty(x))
                .Distinct()
                .ToArray();

            // 保存当前选中项
            string currentSelection = cmb_targetIPs.SelectedItem?.ToString();

            // 更新 ComboBox 数据源
            cmb_targetIPs.ItemsSource = ips;
            cmb_targetIPs.Items.Refresh();

            // 尝试恢复选择，若无效则选第一个
            if (!string.IsNullOrEmpty(currentSelection) && ips.Contains(currentSelection))
            {
                cmb_targetIPs.SelectedItem = currentSelection;
            }
            else if (ips.Length > 0)
            {
                cmb_targetIPs.SelectedIndex = 0;
            }
        }
    }
}