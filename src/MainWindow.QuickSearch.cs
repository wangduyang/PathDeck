using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace QuickFileDesk;

public partial class MainWindow
{
    private const string QuickSearchHint = "输入文件名开始搜索。网络位置需先加入 Everything 索引。";
    private readonly ObservableCollection<EverythingHit> quickHits = [];
    private EverythingSearch? everythingSearch;
    private CancellationTokenSource? quickRequest;
    private TabItem? quickPreviousFolder;
    private static readonly object QuickTab = new();
    private static bool IsQuickTab(TabItem tab) => ReferenceEquals(tab.Tag, QuickTab);
    private bool QuickSearchActive => Tabs?.SelectedItem is TabItem tab && IsQuickTab(tab);

    private void QuickSearchClick(object sender, RoutedEventArgs e)
    {
        if (Tabs.Items.OfType<TabItem>().FirstOrDefault(IsQuickTab) is { } open)
        {
            if (Tabs.SelectedItem == open) QuickQuery.Focus();
            else Tabs.SelectedItem = open;
            return;
        }

        QuickScope.ItemsSource = new[] { new Location("全部已索引位置", "") }.Concat(
            (SidebarDrives.ItemsSource as DriveEntry[] ?? [])
                .Select(drive => new Location(drive.Path.TrimEnd('\\'), drive.Path))).ToArray();
        QuickScope.SelectedIndex = 0;
        QuickAllKinds.IsChecked = true;
        QuickResults.ItemsSource = quickHits;
        QuickStatus.Text = QuickSearchHint;
        QuickMore.Visibility = Visibility.Collapsed;
        AddTabItem(QuickTab, "跨盘搜索", "用 Everything 搜索已索引位置");
    }

    private async Task SearchEverything(bool debounce = false, bool append = false)
    {
        if (!QuickSearchActive || closing) return;
        quickRequest?.Cancel();
        if (!append)
        {
            quickHits.Clear();
            QuickMore.Visibility = Visibility.Collapsed;
        }
        var term = QuickQuery.Text.Trim();
        if (term.Length == 0)
        {
            QuickStatus.Text = QuickSearchHint;
            return;
        }
        if (term.Contains('"'))
        {
            QuickStatus.Text = "请输入文件名，去掉双引号后再试。";
            return;
        }

        using var request = new CancellationTokenSource();
        quickRequest = request;
        QuickMore.IsEnabled = false;
        QuickStatus.Text = "正在搜索…";
        try
        {
            if (debounce) await Task.Delay(160, request.Token);
            var kind = QuickFilesOnly.IsChecked == true ? "file: " : QuickFoldersOnly.IsChecked == true ? "folder: " : "";
            var drive = QuickScope.SelectedValue is string { Length: > 0 } path ? path : null;
            var page = await (everythingSearch ??= new EverythingSearch()).Query(term, kind, drive, quickHits.Count, request.Token);
            if (request.IsCancellationRequested) return;
            foreach (var hit in page.Hits) quickHits.Add(hit);
            var more = page.Hits.Length != 0 && quickHits.Count < page.Total;
            QuickMore.Visibility = more ? Visibility.Visible : Visibility.Collapsed;
            QuickStatus.Text = page.Total == 0
                ? "没有找到匹配项。网络位置需先加入 Everything 索引。"
                : more ? $"已显示 {quickHits.Count:N0} / {page.Total:N0} 项"
                : $"找到 {quickHits.Count:N0} 项";
        }
        catch (OperationCanceledException) { }
        catch (IOException ex)
        {
            if (!request.IsCancellationRequested) QuickStatus.Text = ex.Message;
        }
        catch (TimeoutException)
        {
            if (!request.IsCancellationRequested)
                QuickStatus.Text = "Everything 暂时没有响应。稍后按回车重试。";
        }
        finally
        {
            if (quickRequest == request)
            {
                quickRequest = null;
                QuickMore.IsEnabled = true;
            }
        }
    }

    private void QuickQueryChanged(object sender, TextChangedEventArgs e) => _ = SearchEverything(debounce: true);
    private void QuickFilterChanged(object sender, SelectionChangedEventArgs e) => _ = SearchEverything();
    private void QuickKindChanged(object sender, RoutedEventArgs e) => _ = SearchEverything();
    private void QuickMoreClick(object sender, RoutedEventArgs e) => _ = SearchEverything(append: true);

    private void QuickQueryKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) _ = SearchEverything();
        else if (e.Key == Key.Escape) QuickQuery.Clear();
        else return;
        e.Handled = true;
    }

    private void QuickResultsResized(object sender, SizeChangedEventArgs e)
    {
        if (!e.WidthChanged) return;
        var flexible = Math.Max(200, e.NewSize.Width - 136 - 78 - SystemParameters.VerticalScrollBarWidth - 4);
        QuickNameColumn.Width = Math.Floor(flexible * 0.44);
        QuickFolderColumn.Width = Math.Floor(flexible - QuickNameColumn.Width);
    }

    private async void LocateQuickResultClick(object sender, RoutedEventArgs e)
    {
        if (QuickResults.SelectedItem is EverythingHit hit) await LocateQuickResult(hit);
    }

    private async void OpenQuickDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && e.OriginalSource is DependencyObject element &&
            ItemsControl.ContainerFromElement(QuickResults, element) is ListViewItem { DataContext: EverythingHit hit })
            await OpenQuickResult(hit);
    }

    private async void QuickResultKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || QuickResults.SelectedItem is not EverythingHit hit) return;
        e.Handled = true;
        await OpenQuickResult(hit);
    }

    private async Task OpenQuickResult(EverythingHit hit)
    {
        if (hit.IsFolder)
        {
            await Navigate(hit.FullPath);
            return;
        }

        var entry = new FileEntry(hit.FullPath, hit.Name, false, false, hit.Bytes, hit.Modified ?? DateTime.MinValue);
        if (entry.IsShortcut || NiftiFiles.IsReviewable(entry))
        {
            await LocateQuickResult(hit);
            if (FileList.SelectedItem is FileEntry selected && FileOperations.SamePath(selected.FullPath, hit.FullPath))
                await OpenSelection();
            return;
        }
        ShellStart(new ProcessStartInfo(hit.FullPath) { UseShellExecute = true });
    }

    private async Task LocateQuickResult(EverythingHit hit)
    {
        await Navigate(hit.Folder, selectPath: hit.FullPath);
        if (Current is not { Loaded: true } tab || !FileOperations.SamePath(tab.Path, hit.Folder)) return;
        FileList.Focus();
        if (FileList.SelectedItem is not FileEntry entry || !FileOperations.SamePath(entry.FullPath, hit.FullPath))
            Report("该项未在文件夹中显示。可能已被移动，或需要勾选「隐藏文件」。");
    }
}
