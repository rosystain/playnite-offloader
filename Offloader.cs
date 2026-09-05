using Offloader.Models;
using Offloader.Services;
using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace Offloader
{
    public class Offloader : GenericPlugin
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private const string MenuSection = "Offloader";

        private OffloaderSettingsViewModel settings { get; set; }
        private readonly SyncStateStore store;
        private readonly RobocopySyncService sync = new RobocopySyncService();
        private readonly ConcurrentDictionary<Guid, SemaphoreSlim> gameLocks = new ConcurrentDictionary<Guid, SemaphoreSlim>();

        public override Guid Id { get; } = Guid.Parse("5180751b-c8af-41cf-b9de-76253984e71c");

        public Offloader(IPlayniteAPI api) : base(api)
        {
            settings = new OffloaderSettingsViewModel(this);
            store = new SyncStateStore(GetPluginUserDataPath());
            ApplyArgsFromSettings();
            Properties = new GenericPluginProperties
            {
                HasSettings = true
            };
        }

        internal OffloaderSettings CurrentSettings => settings.Settings;
        internal SyncStateStore Store => store;

        public override IEnumerable<GameMenuItem> GetGameMenuItems(GetGameMenuItemsArgs args)
        {
            var games = args?.Games?.Where(g => g != null).ToList() ?? new List<Game>();
            if (games.Count == 0)
            {
                return Enumerable.Empty<GameMenuItem>();
            }

            var anyNotEnabled = games.Any(g => !store.IsEnabled(g.Id));
            var anyEnabled = games.Any(g => store.IsEnabled(g.Id));
            var anyPushable = games.Any(g => store.IsEnabled(g.Id) && g.IsInstalled && Directory.Exists(SafeLocalPath(g)));
            var anyOffloadable = games.Any(g => store.IsEnabled(g.Id) && g.IsInstalled && !string.IsNullOrWhiteSpace(SafeLocalPath(g)));
            var anyRemote = games.Any(g => store.IsEnabled(g.Id) && HasRemoteData(g));

            var items = new List<GameMenuItem>();
            if (anyNotEnabled)
            {
                items.Add(new GameMenuItem
                {
                    Description = "Offloader：启用同步",
                    MenuSection = MenuSection,
                    Action = a => EnableGames(a.Games)
                });
            }
            if (anyPushable)
            {
                items.Add(new GameMenuItem
                {
                    Description = "Offloader：立即推送到远端",
                    MenuSection = MenuSection,
                    Action = a => PushGamesWithProgress(a.Games)
                });
            }
            if (anyOffloadable)
            {
                items.Add(new GameMenuItem
                {
                    Description = "Offloader：释放本地并标记未安装",
                    MenuSection = MenuSection,
                    Action = a => OffloadGamesWithProgress(a.Games)
                });
            }
            if (anyRemote)
            {
                items.Add(new GameMenuItem
                {
                    Description = "Offloader：打开远端目录",
                    MenuSection = MenuSection,
                    Action = a => OpenRemoteDirs(a.Games)
                });
            }
            if (anyEnabled)
            {
                items.Add(new GameMenuItem
                {
                    Description = "Offloader：禁用同步",
                    MenuSection = MenuSection,
                    Action = a => DisableGames(a.Games)
                });
            }
            return items;
        }

        public override IEnumerable<InstallController> GetInstallActions(GetInstallActionsArgs args)
        {
            var game = args?.Game;
            if (game == null || store.IsEnabled(game.Id) == false || game.IsInstalled)
            {
                return Enumerable.Empty<InstallController>();
            }
            // 远端无数据时不提供恢复入口，避免用户误点
            if (!HasRemoteData(game))
            {
                return Enumerable.Empty<InstallController>();
            }
            return new[] { new OffloaderInstallController(this, game) };
        }

        public override void OnGameStopped(OnGameStoppedEventArgs args)
        {
            try
            {
                var game = args?.Game;
                if (game == null || !CurrentSettings.EnableAutoPushOnStopped || !store.IsEnabled(game.Id) || !game.IsInstalled)
                {
                    return;
                }
                var local = SafeLocalPath(game);
                if (string.IsNullOrWhiteSpace(local) || !Directory.Exists(local))
                {
                    return;
                }
                var gameId = game.Id;
                var gameName = game.Name;
                Task.Run(() =>
                {
                    var gate = gameLocks.GetOrAdd(gameId, _ => new SemaphoreSlim(1, 1));
                    if (!gate.Wait(0))
                    {
                        logger.Info($"Offloader: {gameName} 已有同步在进行，跳过退出后自动推送。");
                        return;
                    }
                    try
                    {
                        ApplyArgsFromSettings();
                        var remote = SyncStateStore.GetRemotePath(CurrentSettings.RemoteRoot, gameId);
                        var res = sync.Push(local, remote, CancellationToken.None, true);
                        if (res.Success)
                        {
                            store.UpdateLastPush(gameId);
                            logger.Info($"Offloader: {gameName} 退出后自动推送成功。");
                        }
                        else
                        {
                            logger.Error($"Offloader: {gameName} 退出后自动推送失败，退出码 {res.ExitCode}。");
                            PlayniteApi.Notifications.Add(gameId + "-autopush", $"Offloader：{gameName} 自动推送失败（robocopy 退出码 {res.ExitCode}），请手动推送。", NotificationType.Error);
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.Error(ex, $"Offloader: {gameName} 退出后自动推送异常。");
                    }
                    finally
                    {
                        gate.Release();
                    }
                });
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Offloader: OnGameStopped 处理异常。");
            }
        }

        #region 菜单动作

        private void EnableGames(List<Game> games)
        {
            if (!RequireRemoteRoot())
            {
                return;
            }
            foreach (var g in games.Where(g => g != null && !store.IsEnabled(g.Id)))
            {
                store.SetEnabled(g.Id, g.InstallDirectory ?? string.Empty);
            }
            PlayniteApi.Notifications.Add("offloader-enabled", $"Offloader：已启用 {games.Count} 个游戏的同步。", NotificationType.Info);
        }

        private void DisableGames(List<Game> games)
        {
            foreach (var g in games.Where(g => g != null && store.IsEnabled(g.Id)))
            {
                store.SetDisabled(g.Id);
            }
        }

        private void PushGamesWithProgress(List<Game> games)
        {
            if (!RequireRemoteRoot())
            {
                return;
            }
            var targets = games.Where(g => g != null && store.IsEnabled(g.Id) && g.IsInstalled && Directory.Exists(SafeLocalPath(g))).ToList();
            if (targets.Count == 0)
            {
                PlayniteApi.Dialogs.ShowErrorMessage("没有可推送的游戏：需已启用同步、已安装且本地目录存在。");
                return;
            }
            ApplyArgsFromSettings();
            PlayniteApi.Dialogs.ActivateGlobalProgress(async progress =>
            {
                foreach (var g in targets)
                {
                    progress.Text = $"Offloader 推送中：{g.Name}";
                    if (progress.CancelToken.IsCancellationRequested)
                    {
                        break;
                    }
                    var gate = gameLocks.GetOrAdd(g.Id, _ => new SemaphoreSlim(1, 1));
                    await gate.WaitAsync(progress.CancelToken);
                    try
                    {
                        var local = SafeLocalPath(g);
                        var remote = SyncStateStore.GetRemotePath(CurrentSettings.RemoteRoot, g.Id);
                        var res = await Task.Run(() => sync.Push(local, remote, progress.CancelToken, true, t => progress.Text = $"Offloader 推送中：{g.Name}\n{t}"));
                        if (!res.Success)
                        {
                            throw new InvalidOperationException($"robocopy 退出码 {res.ExitCode}（>=8 为失败）。");
                        }
                        store.UpdateLastPush(g.Id);
                        store.UpdateLocalPath(g.Id, local);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        logger.Error(ex, $"Offloader: 推送失败：{g.Name}");
                        PlayniteApi.Notifications.Add(g.Id + "-push", $"Offloader：{g.Name} 推送失败：{ex.Message}", NotificationType.Error);
                    }
                    finally
                    {
                        gate.Release();
                    }
                }
            }, new GlobalProgressOptions("Offloader 推送中…", true) { IsIndeterminate = true });
        }

        private void OffloadGamesWithProgress(List<Game> games)
        {
            if (!RequireRemoteRoot())
            {
                return;
            }
            var targets = games.Where(g => g != null && store.IsEnabled(g.Id) && g.IsInstalled && !string.IsNullOrWhiteSpace(SafeLocalPath(g))).ToList();
            if (targets.Count == 0)
            {
                return;
            }
            var confirm = PlayniteApi.Dialogs.ShowMessage(
                $"确定释放以下 {targets.Count} 个游戏的本地文件吗？\n{string.Join("\n", targets.Select(g => "• " + g.Name))}\n\n流程：先做一次最终推送并校验远端，确认远端有数据后才会删除本地整个目录，并将游戏标记为未安装。",
                "Offloader 释放确认",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes)
            {
                return;
            }
            ApplyArgsFromSettings();
            PlayniteApi.Dialogs.ActivateGlobalProgress(async progress =>
            {
                foreach (var g in targets)
                {
                    if (progress.CancelToken.IsCancellationRequested)
                    {
                        break;
                    }
                    var gate = gameLocks.GetOrAdd(g.Id, _ => new SemaphoreSlim(1, 1));
                    await gate.WaitAsync(progress.CancelToken);
                    try
                    {
                        progress.Text = $"Offloader 释放中（终推）：{g.Name}";
                        var local = SafeLocalPath(g);
                        // 五重门：启用 / 路径非空 / 本地是目录 / 远端根合法 / 远端校验通过才删
                        if (string.IsNullOrWhiteSpace(local))
                        {
                            throw new InvalidOperationException("本地路径为空，为防止误删已中止。");
                        }
                        var remote = SyncStateStore.GetRemotePath(CurrentSettings.RemoteRoot, g.Id);
                        if (Directory.Exists(local))
                        {
                            var res = await Task.Run(() => sync.Push(local, remote, progress.CancelToken, false, t => progress.Text = $"Offloader 释放中（终推）：{g.Name}\n{t}"));
                            if (!res.Success)
                            {
                                throw new InvalidOperationException($"最终推送失败，robocopy 退出码 {res.ExitCode}，未删除本地。");
                            }
                        }
                        if (!sync.RemoteHasData(remote))
                        {
                            throw new InvalidOperationException("远端校验未通过（远端为空或不可达），未删除本地。");
                        }
                        progress.Text = $"Offloader 释放中（删除本地）：{g.Name}";
                        if (Directory.Exists(local))
                        {
                            await Task.Run(() => Directory.Delete(local, true));
                        }
                        // 标记未安装，但保留 InstallDirectory 供恢复
                        var dbGame = PlayniteApi.Database.Games.Get(g.Id);
                        var toUpdate = dbGame ?? g;
                        toUpdate.IsInstalled = false;
                        if (string.IsNullOrWhiteSpace(toUpdate.InstallDirectory))
                        {
                            toUpdate.InstallDirectory = local;
                        }
                        PlayniteApi.Database.Games.Update(toUpdate);
                        store.UpdateLocalPath(g.Id, local);
                        store.UpdateLastPush(g.Id);
                        logger.Info($"Offloader: {g.Name} 已释放，远端保留：{remote}");
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        logger.Error(ex, $"Offloader: 释放失败：{g.Name}");
                        PlayniteApi.Notifications.Add(g.Id + "-offload", $"Offloader：{g.Name} 释放失败：{ex.Message}", NotificationType.Error);
                    }
                    finally
                    {
                        gate.Release();
                    }
                }
            }, new GlobalProgressOptions("Offloader 释放中…", true) { IsIndeterminate = true });
        }

        private void OpenRemoteDirs(List<Game> games)
        {
            if (!RequireRemoteRoot())
            {
                return;
            }
            foreach (var g in games.Where(g => g != null && store.IsEnabled(g.Id)))
            {
                var remote = SyncStateStore.GetRemotePath(CurrentSettings.RemoteRoot, g.Id);
                try
                {
                    if (Directory.Exists(remote))
                    {
                        Process.Start(remote);
                    }
                    else
                    {
                        PlayniteApi.Notifications.Add(g.Id + "-remote-missing", $"Offloader：{g.Name} 远端目录不存在。", NotificationType.Error);
                    }
                }
                catch (Exception ex)
                {
                    ReportError($"打开远端目录失败：\n{ex.Message}");
                }
            }
        }

        #endregion

        #region 恢复（供 InstallController 与潜在右键入口复用）

        internal void RestoreGameBlocking(Game game, Playnite.SDK.GlobalProgressActionArgs progress)
        {
            if (game == null)
            {
                throw new ArgumentNullException(nameof(game));
            }
            if (!store.IsEnabled(game.Id))
            {
                throw new InvalidOperationException("该游戏未启用 Offloader 同步。");
            }
            if (!RequireRemoteRoot())
            {
                throw new InvalidOperationException("未设置远端仓库目录。");
            }
            ApplyArgsFromSettings();
            var remote = SyncStateStore.GetRemotePath(CurrentSettings.RemoteRoot, game.Id);
            if (!sync.RemoteHasData(remote))
            {
                throw new DirectoryNotFoundException("远端仓库无此游戏数据：" + remote);
            }

            // 目标：DB 当前值 → 状态快照 → 用户重选
            string target = null;
            try
            {
                var dbGame = PlayniteApi.Database.Games.Get(game.Id);
                target = (dbGame?.InstallDirectory ?? game.InstallDirectory)?.Trim();
            }
            catch
            {
                target = (game.InstallDirectory ?? string.Empty).Trim();
            }
            var snap = store.Get(game.Id)?.LocalPath?.Trim();
            if (string.IsNullOrWhiteSpace(target))
            {
                target = snap;
            }
            if (string.IsNullOrWhiteSpace(target))
            {
                // 约定：用户重选恢复位置
                var picked = PlayniteApi.Dialogs.SelectFolder();
                if (string.IsNullOrWhiteSpace(picked))
                {
                    throw new OperationCanceledException();
                }
                target = picked.Trim();
            }
            else if (!Directory.Exists(target) && !string.IsNullOrWhiteSpace(snap) && !string.Equals(target, snap, StringComparison.OrdinalIgnoreCase))
            {
                // 原路径已不存在且与快照不一致时，给用户一次重选机会（默认回原路径）
                var picked = PlayniteApi.Dialogs.SelectFolder();
                if (!string.IsNullOrWhiteSpace(picked))
                {
                    target = picked.Trim();
                }
            }

            var gate = gameLocks.GetOrAdd(game.Id, _ => new SemaphoreSlim(1, 1));
            gate.Wait(progress?.CancelToken ?? CancellationToken.None);
            try
            {
                var token = progress?.CancelToken ?? CancellationToken.None;
                var res = sync.Pull(remote, target, token, t =>
                {
                    if (progress != null)
                    {
                        progress.Text = $"Offloader 恢复中：{game.Name}\n{t}";
                    }
                });
                if (!res.Success)
                {
                    throw new InvalidOperationException($"robocopy 退出码 {res.ExitCode}（>=8 为失败）。");
                }
                var toUpdate = PlayniteApi.Database.Games.Get(game.Id) ?? game;
                toUpdate.InstallDirectory = target;
                toUpdate.IsInstalled = true;
                PlayniteApi.Database.Games.Update(toUpdate);
                store.UpdateLocalPath(game.Id, target);
                logger.Info($"Offloader: {game.Name} 已恢复到 {target}");
            }
            finally
            {
                gate.Release();
            }
        }

        #endregion

        #region 辅助

        private void ApplyArgsFromSettings()
        {
            sync.PullArgs = string.IsNullOrWhiteSpace(CurrentSettings.PullArgs) ? "/E /R:2 /W:5 /MT:16" : CurrentSettings.PullArgs.Trim();
            sync.PushArgs = string.IsNullOrWhiteSpace(CurrentSettings.PushArgs) ? "/E /XO /IPG:50 /R:1 /W:3 /NP /NDL" : CurrentSettings.PushArgs.Trim();
        }

        private bool RequireRemoteRoot()
        {
            var root = CurrentSettings.RemoteRoot?.Trim();
            if (string.IsNullOrEmpty(root))
            {
                PlayniteApi.Dialogs.ShowErrorMessage("请先在 Offloader 设置中配置远端仓库目录（RemoteRoot）。");
                return false;
            }
            return true;
        }

        private string SafeLocalPath(Game game)
        {
            try
            {
                var p = game.InstallDirectory;
                if (!string.IsNullOrWhiteSpace(p))
                {
                    return p.Trim();
                }
            }
            catch
            {
            }
            try
            {
                return store.Get(game.Id)?.LocalPath?.Trim();
            }
            catch
            {
                return null;
            }
        }

        private bool HasRemoteData(Game game)
        {
            try
            {
                var root = CurrentSettings.RemoteRoot?.Trim();
                if (string.IsNullOrEmpty(root))
                {
                    return false;
                }
                return sync.RemoteHasData(SyncStateStore.GetRemotePath(root, game.Id));
            }
            catch
            {
                return false;
            }
        }

        internal void ReportError(string message)
        {
            try
            {
                PlayniteApi.Dialogs.ShowErrorMessage(message, "Offloader");
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Offloader: 显示错误对话框失败。");
            }
        }

        #endregion

        public override void OnGameInstalled(OnGameInstalledEventArgs args)
        {
        }

        public override void OnGameStarted(OnGameStartedEventArgs args)
        {
        }

        public override void OnGameStarting(OnGameStartingEventArgs args)
        {
        }

        public override void OnGameUninstalled(OnGameUninstalledEventArgs args)
        {
        }

        public override void OnApplicationStarted(OnApplicationStartedEventArgs args)
        {
        }

        public override void OnApplicationStopped(OnApplicationStoppedEventArgs args)
        {
        }

        public override void OnLibraryUpdated(OnLibraryUpdatedEventArgs args)
        {
        }

        public override ISettings GetSettings(bool firstRunSettings)
        {
            return settings;
        }

        public override System.Windows.Controls.UserControl GetSettingsView(bool firstRunSettings)
        {
            return new OffloaderSettingsView();
        }
    }
}
