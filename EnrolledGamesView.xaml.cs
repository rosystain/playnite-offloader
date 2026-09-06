using Offloader.Models;
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
                vm?.Plugin.PlayniteApi.Dialogs.ShowErrorMessage("刷新清单失败：\n" + ex.Message, "Offloader");
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
                vm.Plugin.PlayniteApi.Dialogs.ShowMessage("请先在清单中选择要移除的记录。", "Offloader");
                return;
            }
            var confirm = vm.Plugin.PlayniteApi.Dialogs.ShowMessage(
                "确定删除以下 " + selected.Count + " 条远端记录吗？\n" + string.Join("\n", selected.Take(10).Select(s => "• " + s.DisplayName)) + (selected.Count > 10 ? "\n…" : "") + "\n\n将删除远端整个 GameId 目录并清状态，不可恢复。",
                "Offloader 移除确认",
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
                vm.Plugin.PlayniteApi.Dialogs.ShowErrorMessage("移除失败：\n" + ex.Message, "Offloader");
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
