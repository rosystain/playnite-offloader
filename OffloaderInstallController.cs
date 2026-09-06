using Playnite.SDK;
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
            Name = ResourceProvider.GetString("LOCoffloaderInstallRestore");
        }

        public override void Install(InstallActionArgs args)
        {
            try
            {
                // Install 可能在后台线程回调，确认框/选目录/全局进度必须在 UI 线程弹
                var dispatcher = plugin.PlayniteApi?.MainView?.UIDispatcher;
                if (dispatcher != null && !dispatcher.CheckAccess())
                {
                    dispatcher.Invoke(() => plugin.RestoreGameWithForeground(Game));
                }
                else
                {
                    plugin.RestoreGameWithForeground(Game);
                }
                InvokeOnInstalled(new GameInstalledEventArgs());
            }
            catch (OperationCanceledException)
            {
                InvokeOnInstallationCancelled(new GameInstallationCancelledEventArgs());
            }
            catch (Exception ex)
            {
                plugin.ReportError(ResourceProvider.GetString("LOCoffloaderErrRestoreFailed") + "\n" + ex.Message);
                InvokeOnInstallationCancelled(new GameInstallationCancelledEventArgs());
            }
        }
    }
}
