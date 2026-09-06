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
        /// <summary>最后推送排序键：LastPushUtc.Ticks，未推送为 long.MinValue（排最后）。显示仍用 LastPushText。</summary>
        public long LastPushSortKey { get; set; } = long.MinValue;
        /// <summary>安装状态显示文本：已安装 / 未安装 / —（库中无此游戏）。</summary>
        public string InstallState { get; set; } = string.Empty;
        /// <summary>是否已安装（排序键）。</summary>
        public bool IsInstalled { get; set; } = false;
        public string LocalPath { get; set; } = string.Empty;
    }
}
