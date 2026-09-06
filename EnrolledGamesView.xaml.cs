using Offloader.Models;
using Playnite.SDK;
using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace Offloader
{
    public partial class EnrolledGamesView : UserControl
    {
        public EnrolledGamesView(OffloaderSettingsViewModel vm)
        {
            InitializeComponent();
            DataContext = vm;
            Loaded += (s, e) =>
            {
                UpdateSearchHint();
            };
            try
            {
                vm?.RefreshEnrolled();
            }
            catch
            {
            }
            UpdateSearchHint();
        }

        private void Refresh_Click(object sender, RoutedEventArgs e)
        {
            var vm = DataContext as OffloaderSettingsViewModel;
            try
            {
                vm?.RefreshEnrolled();
            }
            catch (Exception ex)
            {
                vm?.Plugin.PlayniteApi.Dialogs.ShowErrorMessage(ResourceProvider.GetString("LOCoffloaderListRefreshFailed") + "\n" + ex.Message, "Offloader");
            }
        }

        private void Remove_Click(object sender, RoutedEventArgs e)
        {
            var vm = DataContext as OffloaderSettingsViewModel;
            if (vm == null)
            {
                return;
            }
            var selected = EnrolledList.SelectedItems.OfType<EnrolledGameEntry>().ToList();
            if (selected.Count == 0)
            {
                vm.Plugin.PlayniteApi.Dialogs.ShowMessage(ResourceProvider.GetString("LOCoffloaderListRemoveSelectFirst"), "Offloader");
                return;
            }
            var confirm = vm.Plugin.PlayniteApi.Dialogs.ShowMessage(
                string.Format(ResourceProvider.GetString("LOCoffloaderListRemoveHead"), selected.Count)
                + "\n" + string.Join("\n", selected.Take(10).Select(s => "• " + s.DisplayName)) + (selected.Count > 10 ? "\n…" : "")
                + "\n\n" + ResourceProvider.GetString("LOCoffloaderListRemoveNote"),
                ResourceProvider.GetString("LOCoffloaderListRemoveCaption"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes)
            {
                return;
            }
            try
            {
                vm.RemoveEnrolled(selected.Select(s => s.GameId));
            }
            catch (Exception ex)
            {
                vm.Plugin.PlayniteApi.Dialogs.ShowErrorMessage(ResourceProvider.GetString("LOCoffloaderListRemoveFailed") + "\n" + ex.Message, "Offloader");
            }
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateSearchHint();
        }

        private void UpdateSearchHint()
        {
            try
            {
                SearchHint.Visibility = string.IsNullOrEmpty(SearchBox?.Text) ? Visibility.Visible : Visibility.Collapsed;
            }
            catch
            {
            }
        }

        private void SortDir_Click(object sender, RoutedEventArgs e)
        {
            var vm = DataContext as OffloaderSettingsViewModel;
            if (vm != null)
            {
                vm.EnrolledSortDescending = !vm.EnrolledSortDescending;
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Window.GetWindow(this)?.Close();
        }
    }
}
