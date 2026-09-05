using System;

namespace Offloader.Models
{
    public class GameSyncState
    {
        public Guid GameId { get; set; }
        public string LocalPath { get; set; } = string.Empty;
        public DateTime? LastPushUtc { get; set; }
    }
}
