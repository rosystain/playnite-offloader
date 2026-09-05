namespace Offloader.Services
{
    /// <summary>
    /// 同步速度档位。存设置为 int（0=低，1=中，2=高），XAML 直接绑 SelectedIndex，无需转换器。
    /// </summary>
    public enum SyncSpeed
    {
        Low = 0,
        Medium = 1,
        High = 2
    }

    /// <summary>
    /// 档位到 robocopy 参数的唯一映射。基准 flags 写死（推送永不 /MIR）。
    /// 前台：多线程全速（/MT 16/8/4，无 /IPG）。
    /// 后台：/IPG 与 /MT 互斥——只要同时出现（无论 /MT 值多少）robocopy 就报用法错误、退出码 16、
    ///       不复制任何文件。故后台不能带 /MT，靠 /IPG 限速（无 /MT 时 robocopy 天然单线程），
    ///       档位改控 /IPG 间隔。
    /// </summary>
    public static class RobocopyPresets
    {
        public static SyncSpeed Normalize(int value)
        {
            switch (value)
            {
                case 0: return SyncSpeed.Low;
                case 1: return SyncSpeed.Medium;
                case 2: return SyncSpeed.High;
                default: return SyncSpeed.High;
            }
        }

        private static int MtOf(SyncSpeed speed)
        {
            switch (speed)
            {
                case SyncSpeed.Low: return 4;
                case SyncSpeed.Medium: return 8;
                default: return 16;
            }
        }

        public static string GetPullArgs(int speed)
        {
            var mt = MtOf(Normalize(speed));
            return "/E /R:2 /W:5 /MT:" + mt;
        }

        public static string GetForegroundPushArgs(int speed)
        {
            var mt = MtOf(Normalize(speed));
            // 前台故意不加 /NP：需要 robocopy 输出逐文件 % 行做实时进度；/NDL 保留以减少目录噪音
            return "/E /XO /R:1 /W:3 /MT:" + mt + " /NDL";
        }

        public static string GetBackgroundPushArgs(int speed)
        {
            int ipg;
            switch (Normalize(speed))
            {
                case SyncSpeed.Low: ipg = 100; break;
                case SyncSpeed.Medium: ipg = 50; break;
                default: ipg = 25; break;
            }
            // 注意：严禁再叠 /MT——/IPG 与 /MT 互斥，同用直接退出码 16 不传文件。
            return "/E /XO /R:1 /W:3 /IPG:" + ipg + " /NP /NDL";
        }
    }
}
