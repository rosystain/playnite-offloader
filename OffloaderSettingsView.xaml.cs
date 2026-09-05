using System.Windows;
using System.Windows.Controls;

namespace Offloader
{
    public partial class OffloaderSettingsView : UserControl
    {
        public OffloaderSettingsView()
        {
            InitializeComponent();
        }

        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            var vm = DataContext as OffloaderSettingsViewModel;
            using (var dlg = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "选择 Offloader 远端仓库目录",
                ShowNewFolderButton = true
            })
            {
                if (!string.IsNullOrWhiteSpace(vm?.Settings?.RemoteRoot))
                {
                    try
                    {
                        dlg.SelectedPath = vm.Settings.RemoteRoot;
                    }
                    catch
                    {
                    }
                }
                if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK && vm != null)
                {
                    vm.Settings.RemoteRoot = dlg.SelectedPath;
                }
            }
        }
    }
}
