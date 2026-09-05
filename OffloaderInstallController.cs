using Playnite.SDK.Events;
using Playnite.SDK.Plugins;
using System;

namespace Offloader
{
    public class OffloaderInstallController : InstallController
    {
        private readonly Offloader plugin;

        public OffloaderInstallController(Offloader plugin, Playnite.SDK.Models.Game game) : base(game)
        {
            this.plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            Name = "从 Offloader 仓库恢复";
        }

        public override void Install(InstallActionArgs args)
        {
            try
            {
                plugin.RestoreGameBlocking(Game, null);
                InvokeOnInstalled(new GameInstalledEventArgs());
            }
            catch (OperationCanceledException)
            {
                InvokeOnInstallationCancelled(new GameInstallationCancelledEventArgs());
            }
            catch (Exception ex)
            {
                plugin.ReportError("从 Offloader 仓库恢复失败：\n" + ex.Message);
                InvokeOnInstallationCancelled(new GameInstallationCancelledEventArgs());
            }
        }
    }
}
