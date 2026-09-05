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
        // 正在跑后台推送的游戏。GetGameMenuItems 每次打开菜单都会重算，据此隐藏前台推送入口以防冲突。
        private readonly ConcurrentDictionary<Guid, byte> bgRunning = new ConcurrentDictionary<Guid, byte>();
        // 后台推送的取消源。取消只发信号、不释放，所有权归跑任务的那一端（finally 里移除+Dispose）。
        private readonly ConcurrentDictionary<Guid, CancellationTokenSource> bgCts = new ConcurrentDictionary<Guid, CancellationTokenSource>();

        public override Guid Id { get; } = Guid.Parse("5180751b-c8af-41cf-b9de-76253984e71c");

        public Offloader(IPlayniteAPI api) : base(api)
        {
            settings = new OffloaderSettingsViewModel(this);
            store = new SyncStateStore(GetPluginUserDataPath());
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

            var anyNotEnrolled = games.Any(g => !IsEnrolled(g));
            var anyPushable = games.Any(g => IsEnrolled(g) && g.IsInstalled && Directory.Exists(SafeLocalPath(g)) && !bgRunning.ContainsKey(g.Id));
            var anyBgRunning = games.Any(g => bgRunning.ContainsKey(g.Id));
            var anyOffloadable = games.Any(g => IsEnrolled(g) && g.IsInstalled && !string.IsNullOrWhiteSpace(SafeLocalPath(g)));
            var anyRemote = games.Any(g => IsEnrolled(g));

            var items = new List<GameMenuItem>();
            if (anyNotEnrolled)
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
            if (anyBgRunning)
            {
                items.Add(new GameMenuItem
                {
                    Description = "Offloader：取消后台推送",
                    MenuSection = MenuSection,
                    Action = a => CancelBackgroundPushes(a.Games)
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
            return items;
        }

        public override IEnumerable<InstallController> GetInstallActions(GetInstallActionsArgs args)
        {
            var game = args?.Game;
            if (game == null || game.IsInstalled)
            {
                return Enumerable.Empty<InstallController>();
            }
            // 远端无数据时不提供恢复入口，避免用户误点（空目录不算有数据）
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
                if (game == null || !CurrentSettings.EnableAutoPushOnStopped || !IsEnrolled(game) || !game.IsInstalled)
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
                    bgRunning[gameId] = 0;
                    try
                    {
                        var remote = SyncStateStore.GetRemotePath(CurrentSettings.RemoteRoot, gameId);
                        var bgPushArgs = RobocopyPresets.GetBackgroundPushArgs(CurrentSettings.PushSpeed);
                        var res = sync.Push(local, remote, CancellationToken.None, bgPushArgs, true);
                        if (res.Success)
                        {
                            store.UpdateLastPush(gameId);
                            logger.Info($"Offloader: {gameName} 退出后自动推送成功。");
                        }
                        else
                        {
                            logger.Error($"Offloader: {gameName} 退出后自动推送失败，退出码 {res.ExitCode}。");
                            PlayniteApi.Notifications.Add(gameId + "-autopush", $"Offloader：{gameName} 自动推送失败（robocopy 退出码 {res.ExitCode}），请手动推送。{OutputTail(res.Output)}", NotificationType.Error);
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.Error(ex, $"Offloader: {gameName} 退出后自动推送异常。");
                    }
                    finally
                    {
                        bgRunning.TryRemove(gameId, out _);
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
            foreach (var g in games.Where(g => g != null && !IsEnrolled(g)))
            {
                store.EnsureEntry(g.Id, g.InstallDirectory ?? string.Empty);
            }
            PlayniteApi.Notifications.Add("offloader-enabled", $"Offloader：已启用 {games.Count} 个游戏的同步，后台推送已开始。", NotificationType.Info);
            // 启用即后台推送：已安装且本地目录存在的直接开始传，期间前台推送入口自动隐藏
            PushGamesInBackground(games);
        }

        private void PushGamesWithProgress(List<Game> games)
        {
            if (!RequireRemoteRoot())
            {
                return;
            }
            var targets = games.Where(g => g != null && IsEnrolled(g) && g.IsInstalled && Directory.Exists(SafeLocalPath(g))).ToList();
            if (targets.Count == 0)
            {
                PlayniteApi.Dialogs.ShowErrorMessage("没有可推送的游戏：需已启用同步（远端存在 GameId 目录）、已安装且本地目录存在。");
                return;
            }
            var fgArgs = RobocopyPresets.GetForegroundPushArgs(CurrentSettings.PushSpeed);
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
                    if (!gate.Wait(0))
                    {
                        PlayniteApi.Notifications.Add(g.Id + "-push-busy", $"Offloader：{g.Name} 已有同步在进行，已跳过（等后台任务完成后可再推）。", NotificationType.Error);
                        continue;
                    }
                    try
                    {
                        var local = SafeLocalPath(g);
                        var remote = SyncStateStore.GetRemotePath(CurrentSettings.RemoteRoot, g.Id);
                        var res = await Task.Run(() => sync.Push(local, remote, progress.CancelToken, fgArgs, false, t => progress.Text = $"Offloader 推送中：{g.Name}\n{t}"));
                        if (!res.Success)
                        {
                            throw new InvalidOperationException($"robocopy 退出码 {res.ExitCode}（>=8 为失败）。{OutputTail(res.Output)}");
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
            var targets = games.Where(g => g != null && IsEnrolled(g) && g.IsInstalled && !string.IsNullOrWhiteSpace(SafeLocalPath(g))).ToList();
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
            var offloadArgs = RobocopyPresets.GetForegroundPushArgs(CurrentSettings.PushSpeed);
            PlayniteApi.Dialogs.ActivateGlobalProgress(async progress =>
            {
                foreach (var g in targets)
                {
                    if (progress.CancelToken.IsCancellationRequested)
                    {
                        break;
                    }
                    var gate = gameLocks.GetOrAdd(g.Id, _ => new SemaphoreSlim(1, 1));
                    if (!gate.Wait(0))
                    {
                        PlayniteApi.Notifications.Add(g.Id + "-offload-busy", $"Offloader：{g.Name} 已有同步在进行，已跳过释放。", NotificationType.Error);
                        continue;
                    }
                    try
                    {
                        progress.Text = $"Offloader 释放中（终推）：{g.Name}";
                        var local = SafeLocalPath(g);
                        // 四重门：远端根合法 / 本地路径非空 / 本地是目录则终推 / 远端校验通过才删（是否启用已由 IsEnrolled 前置）
                        if (string.IsNullOrWhiteSpace(local))
                        {
                            throw new InvalidOperationException("本地路径为空，为防止误删已中止。");
                        }
                        var remote = SyncStateStore.GetRemotePath(CurrentSettings.RemoteRoot, g.Id);
                        if (Directory.Exists(local))
                        {
                            var res = await Task.Run(() => sync.Push(local, remote, progress.CancelToken, offloadArgs, false, t => progress.Text = $"Offloader 释放中（终推）：{g.Name}\n{t}"));
                            if (!res.Success)
                            {
                                throw new InvalidOperationException($"最终推送失败，robocopy 退出码 {res.ExitCode}，未删除本地。{OutputTail(res.Output)}");
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
            foreach (var g in games.Where(g => g != null && IsEnrolled(g)))
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
            if (!RequireRemoteRoot())
            {
                throw new InvalidOperationException("未设置远端仓库目录。");
            }
            var pullArgs = RobocopyPresets.GetPullArgs(CurrentSettings.PullSpeed);
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
                var res = sync.Pull(remote, target, token, pullArgs, t =>
                {
                    if (progress != null)
                    {
                        progress.Text = $"Offloader 恢复中：{game.Name}\n{t}";
                    }
                });
                if (!res.Success)
                {
                    throw new InvalidOperationException($"robocopy 退出码 {res.ExitCode}（>=8 为失败）。{OutputTail(res.Output)}");
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

        private void PushGamesInBackground(List<Game> games)
        {
            if (!RequireRemoteRoot())
            {
                return;
            }
            var targets = games.Where(g => g != null && g.IsInstalled && Directory.Exists(SafeLocalPath(g))).ToList();
            if (targets.Count == 0)
            {
                PlayniteApi.Dialogs.ShowErrorMessage("没有可推送的游戏：需已安装且本地目录存在。");
                return;
            }
            var bgArgs = RobocopyPresets.GetBackgroundPushArgs(CurrentSettings.PushSpeed);
            foreach (var g in targets)
            {
                var gate = gameLocks.GetOrAdd(g.Id, _ => new SemaphoreSlim(1, 1));
                if (!gate.Wait(0))
                {
                    PlayniteApi.Notifications.Add(g.Id + "-push-bg-busy", $"Offloader：{g.Name} 已有同步在进行，已跳过后台推送。", NotificationType.Error);
                    continue;
                }
                var gameId = g.Id;
                var gameName = g.Name;
                bgRunning[gameId] = 0;
                var cts = new CancellationTokenSource();
                bgCts[gameId] = cts;
                var local = SafeLocalPath(g);
                var remote = SyncStateStore.GetRemotePath(CurrentSettings.RemoteRoot, gameId);
                PlayniteApi.Notifications.Add(gameId + "-push-bg", $"Offloader：{gameName} 正在后台推送…", NotificationType.Info);
                Task.Run(() =>
                {
                    try
                    {
                        var res = sync.Push(local, remote, cts.Token, bgArgs, true);
                        if (res.Success)
                        {
                            store.UpdateLastPush(gameId);
                            store.UpdateLocalPath(gameId, local);
                            PlayniteApi.Notifications.Remove(gameId + "-push-bg");
                            PlayniteApi.Notifications.Add(gameId + "-push-bg-done", $"Offloader：{gameName} 后台推送完成。", NotificationType.Info);
                            logger.Info($"Offloader: {gameName} 后台推送成功。");
                        }
                        else
                        {
                            PlayniteApi.Notifications.Add(gameId + "-push-bg", $"Offloader：{gameName} 后台推送失败（robocopy 退出码 {res.ExitCode}）。{OutputTail(res.Output)}", NotificationType.Error);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        PlayniteApi.Notifications.Remove(gameId + "-push-bg");
                        PlayniteApi.Notifications.Add(gameId + "-push-bg-cancelled", $"Offloader：{gameName} 后台推送已取消，可改用立即推送。", NotificationType.Info);
                        logger.Info($"Offloader: {gameName} 后台推送已取消。");
                    }
                    catch (Exception ex)
                    {
                        logger.Error(ex, $"Offloader: 后台推送失败：{gameName}");
                        PlayniteApi.Notifications.Add(gameId + "-push-bg", $"Offloader：{gameName} 后台推送失败：{ex.Message}", NotificationType.Error);
                    }
                    finally
                    {
                        bgRunning.TryRemove(gameId, out _);
                        if (bgCts.TryRemove(gameId, out var toDispose))
                        {
                            try
                            {
                                toDispose.Dispose();
                            }
                            catch
                            {
                            }
                        }
                        gate.Release();
                    }
                });
            }
        }

        private void CancelBackgroundPushes(List<Game> games)
        {
            var count = 0;
            foreach (var g in games.Where(g => g != null))
            {
                if (bgCts.TryGetValue(g.Id, out var cts))
                {
                    try
                    {
                        cts.Cancel();
                        count++;
                    }
                    catch
                    {
                    }
                }
            }
            if (count > 0)
            {
                PlayniteApi.Notifications.Add("offloader-bg-cancel", $"Offloader：已取消 {count} 个后台推送，远端保留已传部分，下次推送断点续传。", NotificationType.Info);
            }
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

        /// <summary>
        /// 是否已启用：远端存在 GameId 目录即算（空目录也算，靠下次推送自愈）。
        /// </summary>
        private bool IsEnrolled(Game game)
        {
            try
            {
                if (game == null)
                {
                    return false;
                }
                var root = CurrentSettings.RemoteRoot?.Trim();
                if (string.IsNullOrEmpty(root))
                {
                    return false;
                }
                return sync.RemoteExists(SyncStateStore.GetRemotePath(root, game.Id));
            }
            catch
            {
                return false;
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

        // robocopy 失败时把输出尾巴塞进通知，否则只有退出码（如 16）根本看不出原因
        private static string OutputTail(string output)
        {
            if (string.IsNullOrWhiteSpace(output))
            {
                return string.Empty;
            }
            var t = output.Trim();
            const int max = 400;
            if (t.Length > max)
            {
                t = "…" + t.Substring(t.Length - max + 1);
            }
            return "\n" + t;
        }

        #region 设置页已启用清单

        internal List<EnrolledGameEntry> GetEnrolledEntries(out string status)
        {
            var list = new List<EnrolledGameEntry>();
            var root = CurrentSettings.RemoteRoot?.Trim();
            if (string.IsNullOrEmpty(root))
            {
                status = "未设置远端仓库目录。";
                return list;
            }
            List<Guid> ids = new List<Guid>();
            string scanError = null;
            try
            {
                if (!Directory.Exists(root))
                {
                    scanError = "远端仓库目录不存在或不可达：" + root;
                }
                else
                {
                    foreach (var dir in Directory.EnumerateDirectories(root))
                    {
                        Guid id;
                        try
                        {
                            if (Guid.TryParse(Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)), out id))
                            {
                                ids.Add(id);
                            }
                        }
                        catch
                        {
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                scanError = "远端仓库不可达：" + ex.Message;
            }
            if (scanError != null)
            {
                // 远端不可达时回退到本地快照，避免清单全空误导
                foreach (var s in store.GetAll())
                {
                    if (!ids.Contains(s.GameId))
                    {
                        ids.Add(s.GameId);
                    }
                }
            }
            foreach (var id in ids.Distinct())
            {
                var remote = SyncStateStore.GetRemotePath(root, id);
                Game dbGame = null;
                try
                {
                    dbGame = PlayniteApi.Database.Games.Get(id);
                }
                catch
                {
                }
                var snap = store.Get(id);
                var local = (dbGame?.InstallDirectory ?? snap?.LocalPath ?? string.Empty).Trim();
                var hasData = sync.RemoteHasData(remote);
                var exists = sync.RemoteExists(remote);
                list.Add(new EnrolledGameEntry
                {
                    GameId = id,
                    DisplayName = dbGame != null ? dbGame.Name : "未知游戏 (" + id + ")",
                    IsOrphan = dbGame == null,
                    RemoteState = !exists ? "缺失" : (hasData ? "有数据" : "空目录"),
                    LastPushText = snap?.LastPushUtc == null ? "—" : snap.LastPushUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                    LocalPath = local
                });
            }
            list.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
            status = scanError ?? ("共 " + list.Count + " 条已启用记录。");
            return list;
        }

        internal bool RemoveEnrolled(Guid gameId, out string error)
        {
            error = null;
            if (!RequireRemoteRoot())
            {
                error = "未设置远端仓库目录。";
                return false;
            }
            if (bgRunning.ContainsKey(gameId))
            {
                error = "该游戏有后台推送在进行，请先取消后再移除。";
                return false;
            }
            var gate = gameLocks.GetOrAdd(gameId, _ => new SemaphoreSlim(1, 1));
            if (!gate.Wait(0))
            {
                error = "该游戏有同步在进行，请稍后再试。";
                return false;
            }
            try
            {
                var remote = SyncStateStore.GetRemotePath(CurrentSettings.RemoteRoot, gameId);
                if (Directory.Exists(remote))
                {
                    Directory.Delete(remote, true);
                }
                store.Remove(gameId);
                logger.Info("Offloader: 已移除远端记录 " + remote);
                return true;
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Offloader: 移除远端记录失败。");
                error = ex.Message;
                return false;
            }
            finally
            {
                gate.Release();
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
