using Offloader.Models;
using Playnite.SDK;
using Playnite.SDK.Data;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows.Data;

namespace Offloader
{
    public class OffloaderSettings : ObservableObject
    {
        private string remoteRoot = string.Empty;
        private bool enableAutoPushOnStopped = false;
        private int pullSpeed = 2;
        private int pushSpeed = 2;

        public string RemoteRoot { get => remoteRoot; set => SetValue(ref remoteRoot, value); }
        public bool EnableAutoPushOnStopped { get => enableAutoPushOnStopped; set => SetValue(ref enableAutoPushOnStopped, value); }
        /// <summary>0=低，1=中，2=高（默认高）。XAML 绑 SelectedIndex。</summary>
        public int PullSpeed { get => pullSpeed; set => SetValue(ref pullSpeed, value); }
        /// <summary>0=低，1=中，2=高（默认高）。前台全速跑该档，后台自动加 /IPG:50 限速。</summary>
        public int PushSpeed { get => pushSpeed; set => SetValue(ref pushSpeed, value); }
    }

    public class OffloaderSettingsViewModel : ObservableObject, ISettings
    {
        private readonly Offloader plugin;
        internal Offloader Plugin => plugin;
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
            RefreshEnrolled();
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
                if (Settings.PullSpeed < 0 || Settings.PullSpeed > 2)
                {
                    Settings.PullSpeed = 2;
                }
                if (Settings.PushSpeed < 0 || Settings.PushSpeed > 2)
                {
                    Settings.PushSpeed = 2;
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

        #region 已启用清单（远端存在 GameId 目录即算启用）

        private ObservableCollection<EnrolledGameEntry> enrolled = new ObservableCollection<EnrolledGameEntry>();
        public ObservableCollection<EnrolledGameEntry> Enrolled
        {
            get => enrolled;
            set
            {
                enrolled = value;
                OnPropertyChanged();
            }
        }

        private string enrolledStatus = string.Empty;
        public string EnrolledStatus
        {
            get => enrolledStatus;
            set
            {
                enrolledStatus = value;
                OnPropertyChanged();
            }
        }

        private string enrolledFilter = string.Empty;
        /// <summary>清单搜索关键字（游戏名 / GameId，独立窗口的搜索栏绑定它）。</summary>
        public string EnrolledFilter
        {
            get => enrolledFilter;
            set
            {
                enrolledFilter = value;
                OnPropertyChanged();
                ApplyEnrolledFilter();
            }
        }

        public void RefreshEnrolled()
        {
            try
            {
                string status;
                var entries = plugin.GetEnrolledEntries(out status);
                Enrolled = new ObservableCollection<EnrolledGameEntry>(entries ?? new List<EnrolledGameEntry>());
                EnrolledStatus = status ?? string.Empty;
                ApplyEnrolledFilter();
            }
            catch (Exception ex)
            {
                EnrolledStatus = "刷新清单失败：" + ex.Message;
            }
        }

        private void ApplyEnrolledFilter()
        {
            try
            {
                var view = CollectionViewSource.GetDefaultView(Enrolled);
                if (view == null)
                {
                    return;
                }
                var kw = (enrolledFilter ?? string.Empty).Trim();
                if (string.IsNullOrEmpty(kw))
                {
                    view.Filter = null;
                }
                else
                {
                    view.Filter = o =>
                    {
                        var e = o as EnrolledGameEntry;
                        if (e == null)
                        {
                            return false;
                        }
                        if (!string.IsNullOrEmpty(e.DisplayName) && e.DisplayName.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            return true;
                        }
                        return e.GameId.ToString().IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0;
                    };
                }
                view.Refresh();
            }
            catch
            {
            }
        }

        /// <summary>
        /// 移除所选远端记录：删整个 GameId 目录 + 清状态。调用前由界面做二次确认。
        /// </summary>
        public void RemoveEnrolled(IEnumerable<Guid> gameIds)
        {
            if (gameIds == null)
            {
                return;
            }
            var ids = gameIds.ToList();
            if (ids.Count == 0)
            {
                return;
            }
            var ok = 0;
            var fails = new List<string>();
            foreach (var id in ids)
            {
                string name = null;
                try
                {
                    var g = plugin.PlayniteApi.Database.Games.Get(id);
                    name = g?.Name;
                }
                catch
                {
                }
                string error;
                if (plugin.RemoveEnrolled(id, out error))
                {
                    ok++;
                }
                else
                {
                    fails.Add((string.IsNullOrWhiteSpace(name) ? id.ToString() : name) + "：" + error);
                }
            }
            RefreshEnrolled();
            if (fails.Count > 0)
            {
                plugin.PlayniteApi.Dialogs.ShowErrorMessage("部分移除失败：\n" + string.Join("\n", fails.Take(10)));
            }
            else
            {
                plugin.PlayniteApi.Dialogs.ShowMessage("已移除 " + ok + " 条远端记录（目录已删除，不可恢复）。", "Offloader");
            }
        }

        #endregion
    }
}
