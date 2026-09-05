using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;

namespace Offloader.Models
{
    /// <summary>
    /// 按 GameId 持久化每游戏同步状态。远端路径为纯函数，不持久化。
    /// 使用 DataContractJsonSerializer 而非 Playnite Serialization，避免对宿主运行时程序集的依赖。
    /// </summary>
    public class SyncStateStore
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private readonly string filePath;
        private readonly object sync = new object();
        private Dictionary<Guid, GameSyncState> states = new Dictionary<Guid, GameSyncState>();

        public SyncStateStore(string userDataPath)
        {
            filePath = Path.Combine(userDataPath, "states.json");
            Load();
        }

        public static string GetRemotePath(string remoteRoot, Guid gameId)
        {
            return Path.Combine(remoteRoot, gameId.ToString());
        }

        public GameSyncState Get(Guid gameId)
        {
            lock (sync)
            {
                states.TryGetValue(gameId, out var s);
                return s;
            }
        }

        public List<GameSyncState> GetAll()
        {
            lock (sync)
            {
                return states.Values.ToList();
            }
        }

        /// <summary>
        /// 确保条目存在（启用/推送时写快照用）。是否启用以远端目录存在为准，本方法只维护快照。
        /// </summary>
        public void EnsureEntry(Guid gameId, string localPath)
        {
            lock (sync)
            {
                if (!states.TryGetValue(gameId, out var s))
                {
                    s = new GameSyncState { GameId = gameId };
                    states[gameId] = s;
                }
                if (!string.IsNullOrWhiteSpace(localPath))
                {
                    s.LocalPath = localPath;
                }
                SaveLocked();
            }
        }

        public void Remove(Guid gameId)
        {
            lock (sync)
            {
                if (states.Remove(gameId))
                {
                    SaveLocked();
                }
            }
        }

        public void UpdateLocalPath(Guid gameId, string localPath)
        {
            lock (sync)
            {
                if (string.IsNullOrWhiteSpace(localPath))
                {
                    return;
                }
                if (!states.TryGetValue(gameId, out var s))
                {
                    s = new GameSyncState { GameId = gameId };
                    states[gameId] = s;
                }
                s.LocalPath = localPath;
                SaveLocked();
            }
        }

        public void UpdateLastPush(Guid gameId)
        {
            lock (sync)
            {
                if (!states.TryGetValue(gameId, out var s))
                {
                    s = new GameSyncState { GameId = gameId };
                    states[gameId] = s;
                }
                s.LastPushUtc = DateTime.UtcNow;
                SaveLocked();
            }
        }

        private void Load()
        {
            lock (sync)
            {
                try
                {
                    if (File.Exists(filePath))
                    {
                        using (var fs = File.OpenRead(filePath))
                        {
                            var ser = new DataContractJsonSerializer(typeof(List<GameSyncState>));
                            var loaded = ser.ReadObject(fs) as List<GameSyncState>;
                            states = (loaded ?? new List<GameSyncState>()).ToDictionary(s => s.GameId, s => s);
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger.Error(ex, "Offloader: 读取同步状态失败，将使用空状态。");
                    states = new Dictionary<Guid, GameSyncState>();
                }
            }
        }

        private void SaveLocked()
        {
            try
            {
                var dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                using (var fs = File.Create(filePath))
                {
                    var ser = new DataContractJsonSerializer(typeof(List<GameSyncState>));
                    ser.WriteObject(fs, states.Values.ToList());
                }
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Offloader: 保存同步状态失败。");
            }
        }
    }
}
