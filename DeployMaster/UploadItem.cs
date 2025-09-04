using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DeployMaster
{
    public class UploadItem
    {
        public string FullPath { get; set; }         // 完整路径
        public string DisplayName => System.IO.Path.GetFileName(FullPath); // 显示名
        public bool IsFolder { get; set; }           // 是否是文件夹
        public string Icon => IsFolder ? "Resources/FolderIcon.png" : "Resources/FileIcon.png"; // 图标（可选）
    }
}
