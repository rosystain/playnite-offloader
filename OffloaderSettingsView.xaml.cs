using Playnite.SDK;
using System;
using System.Windows;
using System.Windows.Controls;

namespace Offloader
{
    public partial class OffloaderSettingsView : UserControl
    {
        public OffloaderSettingsView()
        {
            InitializeComponent();
            Loaded += (s, e) =>
            {
                try
                {
                    (DataContext as OffloaderSettingsViewModel)?.RefreshEnrolled();
                }
                catch
                {
                }
            };
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
                    try
                    {
                        vm.RefreshEnrolled();
                    }
                    catch
                    {
                    }
                }
            }
        }

        private void RefreshEnrolled_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                (DataContext as OffloaderSettingsViewModel)?.RefreshEnrolled();
            }
            catch (Exception ex)
            {
                MessageBox.Show("刷新清单失败：\n" + ex.Message, "Offloader");
            }
        }

        private void OpenEnrolled_Click(object sender, RoutedEventArgs e)
        {
            var vm = DataContext as OffloaderSettingsViewModel;
            if (vm == null)
            {
                return;
            }
            try
            {
                // 用 Playnite 主题窗口承载，裸 Window 会是系统白底 + 主题浅色字，根本没法看
                var win = vm.Plugin.PlayniteApi.Dialogs.CreateWindow(new WindowCreationOptions
                {
                    ShowMinimizeButton = false,
                    ShowMaximizeButton = false
                });
                win.Title = "Offloader 已启用清单";
                win.Width = 760;
                win.Height = 540;
                win.MinWidth = 560;
                win.MinHeight = 360;
                win.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                win.Owner = Window.GetWindow(this);
                win.Content = new EnrolledGamesView(vm);
                win.ShowDialog();
                vm.RefreshEnrolled();
            }
            catch (Exception ex)
            {
                MessageBox.Show("打开清单失败：\n" + ex.Message, "Offloader");
            }
        }
    }
}
