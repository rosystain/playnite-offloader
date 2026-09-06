namespace Offloader.Services
{
    /// <summary>
    /// robocopy 参数的唯一映射。基准 flags 写死（推送永不 /MIR）。
    /// 无速度档位：吞吐一律全速，让行只靠进程优先级（后台/Pull 用 BelowNormal）。
    /// 前台拉取/推送 /MT:16，后台推送 /MT:8（对 HDD/老 NAS 更温和，同时保持多线程、
    /// 缩短休眠/退出窗口）。不使用 /IPG：/IPG 与 /MT 互斥（同用直接退出码 16），
    /// 且单线程 + /IPG 会把任务拉长数倍。
    /// </summary>
    public static class RobocopyPresets
    {
        public static string GetPullArgs()
        {
            return "/E /R:2 /W:5 /MT:16";
        }

        public static string GetForegroundPushArgs()
        {
            // 前台故意不加 /NP：需要 robocopy 输出逐文件 % 行做实时进度；/NDL 保留以减少目录噪音
            return "/E /XO /R:1 /W:3 /MT:16 /NDL";
        }

        public static string GetBackgroundPushArgs()
        {
            // 后台无进度回调，加 /NP /NDL 减少输出；进程优先级由调用方置 BelowNormal
            return "/E /XO /R:1 /W:3 /MT:8 /NP /NDL";
        }
    }
}
