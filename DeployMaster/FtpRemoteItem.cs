using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DeployMaster
{
    /// <summary>
    /// 表示远端 FTP 上的一个文件或目录
    /// </summary>
    public class FtpRemoteItem
    {
        public string Name { get; set; }           // 文件/目录名
        public string FullName { get; set; }       // 完整路径（如 /apps/config/）
        public bool IsDirectory { get; set; }      // 是否是目录
        public long Size { get; set; }             // 文件大小（目录为 0）
        public DateTime ModifiedDate { get; set; } // 修改时间

        public ObservableCollection<FtpRemoteItem> Children { get; set; } = new ObservableCollection<FtpRemoteItem>();

        // 图标资源（可绑定）
        public string IconKey => IsDirectory ? "FolderIcon" : "FileIcon";
    }
}
