using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace QuickFileDesk;

public partial class MainWindow
{
    private const string QuickSearchHint = "输入文件名开始搜索。";
    private readonly ObservableCollection<IndexedHit> quickHits = [];
    private readonly WindowsSearch windowsSearch = new();
    private EverythingSearch? everythingSearch;
    private CancellationTokenSource? quickRequest;
    private int quickOffset;
    private bool? quickUsesEverything;
    private string? quickScopeDrive;
    private int quickScopeRevision;
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
        AddTabItem(QuickTab, "跨盘搜索", "搜索已索引位置");
        ShowQuickEngine(EverythingSearch.IsRunning, drive: null, refreshScope: true);
    }

    private string? SelectedQuickDrive => QuickScope.SelectedValue is string { Length: > 0 } path ? path : null;

    private void ShowQuickEngine(bool useEverything, string? drive, bool refreshScope = false)
    {
        if (!refreshScope && quickUsesEverything == useEverything && quickScopeDrive == drive) return;
        quickUsesEverything = useEverything;
        quickScopeDrive = drive;
        var revision = ++quickScopeRevision;
        QuickEngineLabel.Text = useEverything ? "搜索引擎 · Everything" : "搜索引擎 · Windows Search";
        var scope = drive is null ? "全部已索引位置" : $"{drive.TrimEnd('\\')} 中已索引的位置";
        QuickIndexScopeLabel.Text = $"索引范围 · {scope}";
        QuickIndexScopeLabel.ToolTip = QuickIndexScopeLabel.Text;
        if (!useEverything) _ = ShowWindowsIndexFolders(drive, revision);
    }

    private async Task ShowWindowsIndexFolders(string? drive, int revision)
    {
        string description;
        try
        {
            var folders = await Task.Run(WindowsIndexScope.ReadFolders);
            if (drive is not null)
                folders = folders.Where(path => string.Equals(Path.GetPathRoot(path), drive, StringComparison.OrdinalIgnoreCase)).ToArray();
            description = folders.Length == 0
                ? "未找到此范围内已配置的文件夹"
                : string.Join("、", folders);
        }
        catch (COMException) { description = "无法读取 Windows 索引范围"; }

        if (closing || revision != quickScopeRevision) return;
        var scope = drive?.TrimEnd('\\') ?? "全部";
        QuickIndexScopeLabel.Text = $"索引范围 · {scope}：{description}";
        QuickIndexScopeLabel.ToolTip = QuickIndexScopeLabel.Text;
    }

    private async Task SearchQuickIndex(bool debounce = false, bool append = false, bool refreshScope = false)
    {
        if (!QuickSearchActive || closing) return;
        quickRequest?.Cancel();
        var drive = SelectedQuickDrive;
        var useEverything = EverythingSearch.IsRunning;
        if (quickUsesEverything != useEverything || quickScopeDrive != drive) append = false;
        ShowQuickEngine(useEverything, drive, refreshScope);
        if (!append)
        {
            quickHits.Clear();
            quickOffset = 0;
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
            bool? folders = QuickFilesOnly.IsChecked == true ? false : QuickFoldersOnly.IsChecked == true ? true : null;
            IndexedPage page;
            if (useEverything)
            {
                try
                {
                    var filter = folders switch { true => "folder: ", false => "file: ", _ => "" };
                    page = await (everythingSearch ??= new EverythingSearch()).Query(term, filter, drive, quickOffset, request.Token);
                }
                catch (Exception ex) when (ex is IOException or TimeoutException &&
                    !request.IsCancellationRequested && !EverythingSearch.IsRunning)
                {
                    useEverything = false;
                    ShowQuickEngine(false, drive);
                    quickHits.Clear();
                    quickOffset = 0;
                    page = await windowsSearch.Query(term, folders, drive, quickOffset, request.Token);
                }
            }
            else page = await windowsSearch.Query(term, folders, drive, quickOffset, request.Token);
            if (request.IsCancellationRequested) return;
            foreach (var hit in page.Hits) quickHits.Add(hit);
            var more = page.NextOffset.HasValue;
            if (page.NextOffset is int next) quickOffset = next;
            QuickMore.Visibility = more ? Visibility.Visible : Visibility.Collapsed;
            QuickStatus.Text = quickHits.Count == 0
                ? useEverything ? "没有找到匹配项。" : "Windows 索引中没有匹配项。新加入的目录可能还在建立索引。"
                : more ? page.Total is int total ? $"已显示 {quickHits.Count:N0} / {total:N0} 项" : $"已显示 {quickHits.Count:N0} 项"
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

    private void QuickQueryChanged(object sender, TextChangedEventArgs e) => _ = SearchQuickIndex(debounce: true);
    private void QuickFilterChanged(object sender, SelectionChangedEventArgs e) => _ = SearchQuickIndex();
    private void QuickKindChanged(object sender, RoutedEventArgs e) => _ = SearchQuickIndex();
    private void QuickMoreClick(object sender, RoutedEventArgs e) => _ = SearchQuickIndex(append: true);

    private void QuickQueryKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) _ = SearchQuickIndex(refreshScope: true);
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
        if (QuickResults.SelectedItem is IndexedHit hit) await LocateQuickResult(hit);
    }

    private async void OpenQuickDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && e.OriginalSource is DependencyObject element &&
            ItemsControl.ContainerFromElement(QuickResults, element) is ListViewItem { DataContext: IndexedHit hit })
            await OpenQuickResult(hit);
    }

    private async void QuickResultKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || QuickResults.SelectedItem is not IndexedHit hit) return;
        e.Handled = true;
        await OpenQuickResult(hit);
    }

    private async Task OpenQuickResult(IndexedHit hit)
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

    private async Task LocateQuickResult(IndexedHit hit)
    {
        await Navigate(hit.Folder, selectPath: hit.FullPath);
        if (Current is not { Loaded: true } tab || !FileOperations.SamePath(tab.Path, hit.Folder)) return;
        FileList.Focus();
        if (FileList.SelectedItem is not FileEntry entry || !FileOperations.SamePath(entry.FullPath, hit.FullPath))
            Report("该项未在文件夹中显示。可能已被移动，或需要勾选「隐藏文件」。");
    }
}
