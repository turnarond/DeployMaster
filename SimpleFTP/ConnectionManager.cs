using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Net;
using System.IO;

namespace SimpleFTP
{
    class ConnectionManager
    {
        public string connResponse;
        public List<FileObject> lines = new List<FileObject>();

        public bool ConnectToFTP(ConnectionProfile connProfile)
        {
            FtpWebRequest request = (FtpWebRequest)WebRequest.Create(connProfile.ConnUri);
            request.Method = WebRequestMethods.Ftp.ListDirectory; // 推荐用 NameOnly，避免解析问题
            request.Credentials = new NetworkCredential(connProfile.ConnUser, connProfile.ConnPass);
            request.UsePassive = connProfile.ConnPassiveMode;
            request.UseBinary = connProfile.ConnBinary;
            request.KeepAlive = connProfile.ConnKeepAlive;

            lines.Clear();
            try
            {
                using (FtpWebResponse response = (FtpWebResponse)request.GetResponse())
                using (Stream responseStream = response.GetResponseStream())
                using (StreamReader reader = new StreamReader(responseStream, Encoding.UTF8)) // 注意编码
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        line = line.Trim();
                        if (string.IsNullOrEmpty(line)) continue;

                        // 使用 NameOnly 模式，只获取文件名
                        lines.Add(new FileObject(line, "未知", "未知"));
                    }
                }

                connResponse = $"获取文件列表成功，共 {lines.Count} 个文件。";
                return true;
            }
            catch (WebException ex)
            {
                using (var response = ex.Response as FtpWebResponse)
                {
                    connResponse = $"连接失败: {response?.StatusDescription ?? ex.Message}";
                }
                return false;
            }
            catch (Exception ex)
            {
                connResponse = $"连接异常: {ex.Message}";
                return false;
            }
        }

        // Parses http stream response into a FileObject
        public FileObject ParseResponseObjects(string line)
        {
            string[] splitFile = line.Split(new char[0], StringSplitOptions.RemoveEmptyEntries);
            return new FileObject(splitFile[8], splitFile[4], splitFile[6]);
        }

        public static string FtpUpload(string uri, string userName, string password, string filePath)
        {
            try
            {
                Uri serverUri = new Uri(uri.TrimEnd('/') + "/" + Path.GetFileName(filePath));
                FileInfo fileInfo = new FileInfo(filePath);
                long fileSize = fileInfo.Length;

                if (fileSize == 0)
                    throw new IOException("文件为空");

                FtpWebRequest request = (FtpWebRequest)WebRequest.Create(serverUri);
                request.Method = WebRequestMethods.Ftp.UploadFile;
                request.Credentials = new NetworkCredential(userName, password);
                request.UseBinary = true;
                request.UsePassive = true;

                using (FileStream fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read))
                using (Stream requestStream = request.GetRequestStream())
                {
                    byte[] buffer = new byte[8192]; // 建议：增大缓冲区到 8KB
                    long totalBytesRead = 0;

                    // ✅ 记录上次输出日志的进度（百分比）
                    int lastReportedPercent = -1;

                    int bytesRead;
                    while ((bytesRead = fileStream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        requestStream.Write(buffer, 0, bytesRead);
                        totalBytesRead += bytesRead;

                        // ✅ 计算当前进度百分比
                        int currentPercent = (int)((double)totalBytesRead / fileSize * 100);

                        // ✅ 只有当进度变化 ≥ 1% 时才输出日志
                        if (currentPercent != lastReportedPercent && currentPercent % 1 == 0) // 每1%输出一次
                        {
                            string message = $"📤 上传 {fileInfo.Name}: {currentPercent}% ({FormatFileSize(totalBytesRead)}/{FormatFileSize(fileSize)})";

                            if (MainWindow.mainWindow != null)
                            {
                                MainWindow.mainWindow.AppendLog(message);
                            }

                            lastReportedPercent = currentPercent;
                        }
                    }

                    // ✅ 确保最后100%也输出（防止卡在99%）
                    if (lastReportedPercent != 100)
                    {
                        string message = $"📤 上传 {fileInfo.Name}: 100% ({FormatFileSize(fileSize)}/{FormatFileSize(fileSize)})";
                        MainWindow.mainWindow?.AppendLog(message);
                    }
                }

                // 获取响应
                using (FtpWebResponse response = (FtpWebResponse)request.GetResponse())
                {
                    string successMsg = $"✅ 上传成功: {response.StatusDescription}";
                    MainWindow.mainWindow?.AppendLog(successMsg);
                    return successMsg;
                }
            }
            catch (Exception ex)
            {
                string errorMsg = $"❌ 上传失败: {ex.Message}";
                MainWindow.mainWindow?.AppendLog(errorMsg);
                return errorMsg;
            }
        }

        // ✅ 辅助方法：格式化文件大小（如 1.23 MB）
        private static string FormatFileSize(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB" };
            double size = bytes;
            int unitIndex = 0;

            while (size >= 1024 && unitIndex < units.Length - 1)
            {
                size /= 1024;
                unitIndex++;
            }

            return $"{size:0.##} {units[unitIndex]}";
        }
        public static string FtpDownload(string uri, string userName, string password, string remoteFileName, string localPath, long fileSize)
        {
            try
            {
                Uri serverUri = new Uri(uri.TrimEnd('/') + "/" + remoteFileName);
                string localFilePath = Path.Combine(localPath, remoteFileName);

                FtpWebRequest request = (FtpWebRequest)WebRequest.Create(serverUri);
                request.Method = WebRequestMethods.Ftp.DownloadFile;
                request.Credentials = new NetworkCredential(userName, password);
                request.UseBinary = true;
                request.UsePassive = true;

                using (FtpWebResponse response = (FtpWebResponse)request.GetResponse())
                using (Stream responseStream = response.GetResponseStream())
                using (FileStream fileStream = new FileStream(localFilePath, FileMode.Create, FileAccess.Write))
                {
                    byte[] buffer = new byte[1024];
                    long totalBytesRead = 0;
                    int bytesRead;

                    while ((bytesRead = responseStream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        fileStream.Write(buffer, 0, bytesRead);
                        totalBytesRead += bytesRead;

                        // 报告进度（线程安全）
                        double progress = fileSize > 0 ? (double)totalBytesRead / fileSize * 100 : 0;
                        UpdateProgressOnUI($"下载进度: {Path.GetFileName(remoteFileName)} - {progress:0.00}%");
                    }
                }

                return "下载完成";
            }
            catch (Exception ex)
            {
                return $"下载失败: {ex.Message}";
            }
        }

        // 线程安全地更新 UI
        private static void UpdateProgressOnUI(string message)
        {
            if (MainWindow.mainWindow != null && !MainWindow.mainWindow.Dispatcher.CheckAccess())
            {
                MainWindow.mainWindow.Dispatcher.Invoke(() => MainWindow.mainWindow.AppendLog(message));
            }
            else if (MainWindow.mainWindow != null)
            {
                MainWindow.mainWindow.AppendLog(message);
            }
        }

        public Uri ParseConnectionUrl(string url)
        {
            Console.WriteLine(url);
            if (url.StartsWith("ftp://"))
            {
                Console.WriteLine("Parsed URL: " + url);

                return new Uri(url);
            }
            else
            {
                string formattedUrl = "ftp://" + url;
                Console.WriteLine("Parsed URL: " + formattedUrl);
                return new Uri(formattedUrl);
            }
        }
    }
}
