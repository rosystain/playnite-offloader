using Playnite.SDK;
using Playnite.SDK.Data;
using System;
using System.Collections.Generic;
using System.IO;

namespace Offloader
{
    public class OffloaderSettings : ObservableObject
    {
        private string remoteRoot = string.Empty;
        private bool enableAutoPushOnStopped = false;
        private string pullArgs = "/E /R:2 /W:5 /MT:16";
        private string pushArgs = "/E /XO /IPG:50 /R:1 /W:3 /NP /NDL";

        public string RemoteRoot { get => remoteRoot; set => SetValue(ref remoteRoot, value); }
        public bool EnableAutoPushOnStopped { get => enableAutoPushOnStopped; set => SetValue(ref enableAutoPushOnStopped, value); }
        public string PullArgs { get => pullArgs; set => SetValue(ref pullArgs, value); }
        public string PushArgs { get => pushArgs; set => SetValue(ref pushArgs, value); }
    }

    public class OffloaderSettingsViewModel : ObservableObject, ISettings
    {
        private readonly Offloader plugin;
        private OffloaderSettings editingClone { get; set; }

        private OffloaderSettings settings;
        public OffloaderSettings Settings
        {
            get => settings;
            set
            {
                settings = value;
                OnPropertyChanged();
            }
        }

        public OffloaderSettingsViewModel(Offloader plugin)
        {
            this.plugin = plugin;
            var savedSettings = plugin.LoadPluginSettings<OffloaderSettings>();
            Settings = savedSettings ?? new OffloaderSettings();
        }

        public void BeginEdit()
        {
            editingClone = Serialization.GetClone(Settings);
        }

        public void CancelEdit()
        {
            Settings = editingClone;
        }

        public void EndEdit()
        {
            // 规范化：去尾空格，RemoteRoot 去尾部斜杠（保留 UNC 根 "\\server\share" 形态）
            if (Settings != null)
            {
                Settings.RemoteRoot = (Settings.RemoteRoot ?? string.Empty).Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                Settings.PullArgs = (Settings.PullArgs ?? string.Empty).Trim();
                Settings.PushArgs = (Settings.PushArgs ?? string.Empty).Trim();
                if (string.IsNullOrEmpty(Settings.PullArgs))
                {
                    Settings.PullArgs = "/E /R:2 /W:5 /MT:16";
                }
                if (string.IsNullOrEmpty(Settings.PushArgs))
                {
                    Settings.PushArgs = "/E /XO /IPG:50 /R:1 /W:3 /NP /NDL";
                }
            }
            plugin.SavePluginSettings(Settings);
        }

        public bool VerifySettings(out List<string> errors)
        {
            errors = new List<string>();
            var root = Settings?.RemoteRoot?.Trim();
            if (string.IsNullOrEmpty(root))
            {
                errors.Add("必须设置远端仓库目录（RemoteRoot），例如 \\\\NAS\\games\\Offloader 或 D:\\GameArchive。");
                return false;
            }
            try
            {
                if (!Path.IsPathRooted(root))
                {
                    errors.Add("远端仓库目录必须是绝对路径。");
                    return false;
                }
            }
            catch (Exception ex)
            {
                errors.Add("远端仓库目录不合法：" + ex.Message);
                return false;
            }
            return true;
        }
    }
}
