using System;

namespace Offloader.Models
{
    /// <summary>
    /// 设置页已启用清单的行：是否启用以远端目录存在为准，空目录也算。
    /// </summary>
    public class EnrolledGameEntry
    {
        public Guid GameId { get; set; }
        public string DisplayName { get; set; } = string.Empty;
        public bool IsOrphan { get; set; }
        public string RemoteState { get; set; } = string.Empty;
        public string LastPushText { get; set; } = string.Empty;
        public string LocalPath { get; set; } = string.Empty;
    }
}
