using Offloader.Models;
using Playnite.SDK;
using Playnite.SDK.Data;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.ComponentModel;
using System.Windows.Data;

namespace Offloader
{
    public class OffloaderSettings : ObservableObject
    {
        private string remoteRoot = string.Empty;

        public string RemoteRoot { get => remoteRoot; set => SetValue(ref remoteRoot, value); }
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
            }
            plugin.SavePluginSettings(Settings);
        }

        public bool VerifySettings(out List<string> errors)
        {
            errors = new List<string>();
            var root = Settings?.RemoteRoot?.Trim();
            if (string.IsNullOrEmpty(root))
            {
                errors.Add(ResourceProvider.GetString("LOCoffloaderVerifyRootRequired"));
                return false;
            }
            try
            {
                if (!Path.IsPathRooted(root))
                {
                    errors.Add(ResourceProvider.GetString("LOCoffloaderVerifyRootAbsolute"));
                    return false;
                }
            }
            catch (Exception ex)
            {
                errors.Add(string.Format(ResourceProvider.GetString("LOCoffloaderVerifyRootInvalid"), ex.Message));
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
                ApplyEnrolledSort();
            }
            catch (Exception ex)
            {
                EnrolledStatus = ResourceProvider.GetString("LOCoffloaderListRefreshFailed") + "\n" + ex.Message;
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

        private int enrolledSortIndex = 0;
        /// <summary>清单排序键：0=游戏名，1=最后推送，2=安装状态。弹窗排序下拉框双向绑定它。</summary>
        public int EnrolledSortIndex
        {
            get => enrolledSortIndex;
            set
            {
                if (enrolledSortIndex != value)
                {
                    enrolledSortIndex = value;
                    OnPropertyChanged();
                    ApplyEnrolledSort();
                }
            }
        }

        private bool enrolledSortDescending = false;
        /// <summary>清单是否降序。弹窗升/降序按钮切换它，内存态，不持久化。</summary>
        public bool EnrolledSortDescending
        {
            get => enrolledSortDescending;
            set
            {
                if (enrolledSortDescending != value)
                {
                    enrolledSortDescending = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(SortDirectionText));
                    ApplyEnrolledSort();
                }
            }
        }

        public string SortDirectionText => EnrolledSortDescending ? ResourceProvider.GetString("LOCoffloaderListSortDesc") : ResourceProvider.GetString("LOCoffloaderListSortAsc");

        private void ApplyEnrolledSort()
        {
            try
            {
                var view = CollectionViewSource.GetDefaultView(Enrolled);
                if (view == null)
                {
                    return;
                }
                var dir = EnrolledSortDescending ? ListSortDirection.Descending : ListSortDirection.Ascending;
                using (view.DeferRefresh())
                {
                    view.SortDescriptions.Clear();
                    if (enrolledSortIndex == 2)
                    {
                        view.SortDescriptions.Add(new SortDescription(nameof(EnrolledGameEntry.IsInstalled), dir));
                        view.SortDescriptions.Add(new SortDescription(nameof(EnrolledGameEntry.DisplayName), ListSortDirection.Ascending));
                    }
                    else if (enrolledSortIndex == 1)
                    {
                        view.SortDescriptions.Add(new SortDescription(nameof(EnrolledGameEntry.LastPushSortKey), dir));
                        view.SortDescriptions.Add(new SortDescription(nameof(EnrolledGameEntry.DisplayName), ListSortDirection.Ascending));
                    }
                    else
                    {
                        view.SortDescriptions.Add(new SortDescription(nameof(EnrolledGameEntry.DisplayName), dir));
                    }
                }
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
                    fails.Add(string.Format(ResourceProvider.GetString("LOCoffloaderFmtNamedError"), (string.IsNullOrWhiteSpace(name) ? id.ToString() : name), error));
                }
            }
            RefreshEnrolled();
            if (fails.Count > 0)
            {
                plugin.PlayniteApi.Dialogs.ShowErrorMessage(ResourceProvider.GetString("LOCoffloaderListRemovePartial") + "\n" + string.Join("\n", fails.Take(10)));
            }
            else
            {
                plugin.PlayniteApi.Dialogs.ShowMessage(string.Format(ResourceProvider.GetString("LOCoffloaderListRemoveDone"), ok), "Offloader");
            }
        }

        #endregion
    }
}
