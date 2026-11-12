using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;

namespace QuickFileDesk;

public partial class MainWindow : Window
{
    private enum Navigation { Open, Back, Forward, Refresh, Search }
    private CancellationTokenSource? listing;
    private CancellationTokenSource? preview;
    private string? search;
    private string sortBy = "名称";
    private bool descending;
    private bool working;
    private bool closing;
    private Point? dragStart;
    private string[]? dragPaths;
    private readonly MenuItem menuOpen, menuCut, menuCopy, menuRename, menuDelete, menuPin, menuPath;
    private BrowserTab? Current => (Tabs.SelectedItem as TabItem)?.Tag as BrowserTab;
    private FileEntry[] Selection => FileList.SelectedItems.Cast<FileEntry>().ToArray();

    private readonly string? startupFolder;

    public MainWindow() : this(Environment.GetCommandLineArgs().Skip(1).FirstOrDefault()) { }

    internal MainWindow(string? startupFolder)
    {
        this.startupFolder = startupFolder;
        InitializeComponent();
        fileAgeTimer.Tick += (_, _) => UpdateFileAge();
        liveRefreshTimer.Tick += (_, _) => _ = RefreshChangedDirectory();
        SetIconSize(iconSize);
        Resources["ShowFileThumbnails"] = false;
        colorMenu = CreateColorMenu();
        FileList.ContextMenu = new ContextMenu
        {
            Items =
            {
                colorMenu,
                new Separator(),
                (menuOpen = Menu("打开", "Enter", () => _ = OpenSelection(), "\uED25")),
                (openWithMenu = Menu("打开方式…", "", OpenWithSelection, "\uE7AC")),
                new Separator(),
                (menuCut = Menu("剪切", "Ctrl+X", () => CopyToClipboard(true), "\uE8C6", "IconTeal")),
                (menuCopy = Menu("复制", "Ctrl+C", () => CopyToClipboard(false), "\uE8C8", "IconTeal")),
                Menu("粘贴", "Ctrl+V", () => _ = Paste(), "\uE77F", "IconTeal"),
                new Separator(),
                (menuRename = Menu("重命名", "F2", () => _ = Rename(), "\uE70F", "IconViolet")),
                (menuDelete = Menu("移到回收站", "Delete", () => _ = Recycle(), "\uE74D", "IconRed")),
                new Separator(),
                (compressMenu = CreateCompressMenu()),
                (extractMenu = Menu("解压到同名文件夹", "", () => _ = ExtractSelection(), "\uE8DE", "IconGold")),
                new Separator(),
                Menu("新建文件夹", "Ctrl+Shift+N", () => _ = CreateItem(true), "\uE8F4", "IconGreen"),
                Menu("新建文本文档", "", () => _ = CreateItem(false), "\uE7C3", "IconGreen"),
                new Separator(),
                (menuPin = Menu("固定到经常访问", "", () =>
                {
                    if (FileList.SelectedItem is FileEntry { IsDirectory: true } entry) Pin(entry.FullPath);
                }, "\uE718", "IconViolet")),
                (menuPath = Menu("复制路径", "Ctrl+Shift+C", CopyPaths, "\uE71B", "IconTeal")),
                Menu("属性", "Alt+Enter", ShowProperties, "\uE946", "IconSlate"),
                Menu("在资源管理器中打开", "", OpenExplorer, "\uE8A7")
            }
        };
    }

    private void Start(object sender, RoutedEventArgs e)
    {
        UpdateFileAge();
        fileAgeTimer.Start();
        liveRefreshTimer.Start();
        foreach (var (place, iconColor) in new (Location Place, string IconColor)[]
        {
            (new("桌面", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "\uE7F4"), "IconBlue"),
            (new("下载", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"), "\uE896"), "IconGreen"),
            (new("文档", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)), "IconGold"),
            (new("图片", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "\uEB9F"), "IconViolet")
        })
        {
            if (!string.IsNullOrEmpty(place.Path)) AddLocation(PlacesPanel, place, iconColor: iconColor);
        }
        try { colors = FileColors.Load(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Report($"无法加载颜色标签（{ex.Message}）");
        }
        try { ShowPins(PinnedLocations.Load()); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Report($"无法加载经常访问（{ex.Message}）");
        }
        LoadFileMenu();
        var folder = startupFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (folder != BrowserTab.DriveOverviewPath) ShowDrives(DriveInfo.GetDrives());
        AddTab(folder == BrowserTab.DriveOverviewPath ? folder : Path.GetFullPath(folder));
        FocusLocation();
    }

    private void ShowDrives(DriveInfo[] drives)
    {
        var previous = DriveList.ItemsSource as DriveEntry[] ?? [];
        var entries = drives.Select(drive =>
            previous.FirstOrDefault(entry => entry.Path == drive.Name) ?? new DriveEntry(drive)).ToArray();
        if (!entries.SequenceEqual(previous)) DriveList.ItemsSource = SidebarDrives.ItemsSource = entries;
        foreach (var entry in entries) _ = entry.LoadCapacity();
    }

    internal void ShowDriveOverview()
    {
        EndAddressEdit();
        var overview = Tabs.Items.OfType<TabItem>().FirstOrDefault(item => item.Tag is BrowserTab { IsDriveOverview: true });
        if (overview is null) AddTab(BrowserTab.DriveOverviewPath);
        else if (Tabs.SelectedItem != overview) Tabs.SelectedItem = overview;
        else _ = Refresh();
        DriveList.Focus();
    }

    private void FocusLocation()
    {
        if (Current?.IsDriveOverview == true) DriveList.Focus();
        else FileList.Focus();
    }

    private async void OpenSidebarDrive(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: DriveEntry drive }) await Navigate(drive.Path);
    }

    private async void OpenDriveDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && e.OriginalSource is DependencyObject element &&
            ItemsControl.ContainerFromElement(DriveList, element) is ListBoxItem { DataContext: DriveEntry drive })
            await Navigate(drive.Path);
    }

    private void OpenSettings(object sender, RoutedEventArgs e)
    {
        var window = new SettingsWindow { Owner = this, Icon = Icon };
        (Application.Current as App)?.TrackWindowTheme(window);
        window.Resources[typeof(Button)] = FindResource(typeof(Button));
        window.ShowDialog();
    }

    private void CheckClose(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (working)
        {
            e.Cancel = true;
            Report("文件操作进行中，完成后可关闭窗口。");
            return;
        }
        e.Cancel = OwnedWindows.OfType<ImageViewerWindow>().Any(viewer => !viewer.ConfirmClose());
    }

    private void Stop(object? sender, EventArgs e)
    {
        closing = true;
        fileAgeTimer.Stop();
        liveRefreshTimer.Stop();
        SetDirectoryWatch(null);
        listing?.Cancel();
        preview?.Cancel();
        quickRequest?.Cancel();
        everythingSearch?.Dispose();
    }

    private void AddTab(string path)
    {
        var name = FolderLabel(path);
        AddTabItem(new BrowserTab(path), name, path == BrowserTab.DriveOverviewPath ? name : path);
    }

    private void AddTabItem(object state, string name, string tooltip)
    {
        var label = new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 176 };
        var close = new Button { Content = "×", Margin = new(7, 0, 0, 0), Padding = new(4, 0, 4, 0), ToolTip = "关闭标签页" };
        var header = new StackPanel { Orientation = Orientation.Horizontal, Children = { label, close } };
        var item = new TabItem { Header = header, Tag = state, ToolTip = tooltip };
        close.Click += (_, _) => CloseTab(item);
        Tabs.Items.Add(item);
        Tabs.SelectedItem = item;
    }

    private void CloseTab(TabItem item)
    {
        if (Tabs.Items.Count <= 1 || (Tabs.Items.Count == 2 && !IsQuickTab(item) && Tabs.Items.OfType<TabItem>().Any(IsQuickTab))) return;
        Tabs.Items.Remove(item);
        if (IsQuickTab(item))
        {
            quickRequest?.Cancel();
            QuickQuery.Clear();
            quickHits.Clear();
        }
    }

    private void ChangeTab(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != Tabs || Tabs.SelectedItem is not TabItem item) return;
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (Tabs.SelectedItem == item) item.BringIntoView();
        }, DispatcherPriority.Loaded);
        listing?.Cancel();
        var quick = IsQuickTab(item);
        FolderPage.Visibility = quick ? Visibility.Collapsed : Visibility.Visible;
        QuickSearchPage.Visibility = quick ? Visibility.Visible : Visibility.Collapsed;
        if (quick)
        {
            quickPreviousFolder = e.RemovedItems.OfType<TabItem>().FirstOrDefault();
            Report("");
            CountText.Text = "";
            _ = Dispatcher.InvokeAsync(() => { if (QuickSearchActive) QuickQuery.Focus(); }, DispatcherPriority.Loaded);
            return;
        }
        if (item.Tag is not BrowserTab tab) return;
        if (item == quickPreviousFolder)
        {
            quickPreviousFolder = null;
            if (!tab.IsDriveOverview && tab.Loaded)
            {
                CountSelection();
                _ = RefreshChangedDirectory();
            }
            else _ = Navigate(tab.Path, Navigation.Refresh);
            return;
        }
        quickPreviousFolder = null;
        SearchBox.Text = "";
        search = null;
        UpdateAddress(tab.Path);
        UpdateLocationView(tab.IsDriveOverview);
        DisplayEntries(tab.Entries);
        UpdateNavigation();
        _ = Navigate(tab.Path, Navigation.Refresh);
    }

    private async Task Navigate(string input, Navigation navigation = Navigation.Open, string? query = null, string? selectPath = null)
    {
        if (QuickSearchActive && navigation == Navigation.Open) AddTab(BrowserTab.DriveOverviewPath);
        if (Current is not { } tab) return;
        var showDrives = input == BrowserTab.DriveOverviewPath;
        var path = input;
        try
        {
            if (!showDrives)
            {
                var expanded = Environment.ExpandEnvironmentVariables(input.Trim().Trim('"'));
                if (tab.IsDriveOverview)
                {
                    if (expanded.Length == 2 && char.IsAsciiLetter(expanded[0]) && expanded[1] == ':') expanded += "\\";
                    if (!Path.IsPathFullyQualified(expanded))
                    {
                        Report("请输入完整的文件夹路径。");
                        return;
                    }
                    path = Path.GetFullPath(expanded);
                }
                else path = Path.GetFullPath(expanded, tab.Path);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            Report($"路径无效（{ex.Message}）");
            return;
        }

        listing?.Cancel();
        using var request = new CancellationTokenSource();
        listing = request;
        if (!showDrives) Report(query is null ? "正在加载文件夹…" : $"正在搜索「{query}」…");
        var needsWatch = !showDrives && directoryWatch?.Matches(path, query is not null) != true;
        DirectoryWatch? preparedWatch = null;
        try
        {
            var drives = showDrives ? DriveInfo.GetDrives() : null;
            var entries = showDrives ? Array.Empty<FileEntry>()
                : await Task.Run(() =>
                {
                    if (needsWatch) preparedWatch = new DirectoryWatch(path, query is not null);
                    return BrowserData.List(path, query, request.Token);
                }, request.Token);
            if (request.IsCancellationRequested || closing || Current != tab) return;
            if (showDrives || needsWatch)
            {
                SetDirectoryWatch(preparedWatch);
                preparedWatch = null;
            }
            var locationTypeChanged = tab.IsDriveOverview != showDrives;
            if (!FileOperations.SamePath(tab.Path, path))
            {
                switch (navigation)
                {
                    case Navigation.Back:
                        tab.Back.RemoveAt(tab.Back.Count - 1);
                        tab.Forward.Add(tab.Path);
                        break;
                    case Navigation.Forward:
                        tab.Forward.RemoveAt(tab.Forward.Count - 1);
                        tab.Back.Add(tab.Path);
                        break;
                    default:
                        if (tab.Loaded || tab.IsDriveOverview) tab.Back.Add(tab.Path);
                        tab.Forward.Clear();
                        break;
                }
            }
            tab.Path = path;
            tab.Loaded = !showDrives;
            if (query is null) tab.Entries = entries;
            search = query;
            UpdateAddress(path);
            SearchBox.Text = query ?? "";
            if (Tabs.SelectedItem is TabItem { Header: StackPanel header } item && header.Children[0] is TextBlock label)
            {
                label.Text = FolderLabel(path);
                item.ToolTip = showDrives ? label.Text : path;
            }
            if (drives is not null) ShowDrives(drives);
            UpdateLocationView(showDrives);
            DisplayEntries(entries, selectPath);
            UpdateNavigation();
            if (locationTypeChanged) FocusLocation();
            Report(query is null ? "" : $"搜索结果 · {query}");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            if (!request.IsCancellationRequested && !closing) Report($"无法打开文件夹（{ex.Message}）");
        }
        finally
        {
            preparedWatch?.Dispose();
            if (listing == request) listing = null;
        }
    }

    private void UpdateLocationView(bool showDrives)
    {
        FileListPane.Visibility = showDrives ? Visibility.Collapsed : Visibility.Visible;
        DrivePage.Visibility = showDrives ? Visibility.Visible : Visibility.Collapsed;
        FileActions.IsEnabled = FileViewOptions.IsEnabled = HiddenToggle.IsEnabled = FolderSearch.IsEnabled = !showDrives;
    }

    private void DisplayEntries(FileEntry[] entries, string? selectPath = null, bool preserveView = false)
    {
        UpdateFileAge();
        foreach (var entry in entries) entry.TagColor = colors.GetValueOrDefault(entry.FullPath);
        IEnumerable<FileEntry> visible = entries;
        if (HiddenToggle.IsChecked != true) visible = visible.Where(entry => !entry.Hidden);
        var byFolder = visible.OrderBy(entry => foldersFirst && !entry.IsDirectory);
        var displayed = (sortBy switch
        {
            "修改日期" => descending ? byFolder.ThenByDescending(entry => entry.Updated) : byFolder.ThenBy(entry => entry.Updated),
            ModifiedAgeHeader => descending ? byFolder.ThenBy(entry => entry.Updated) : byFolder.ThenByDescending(entry => entry.Updated),
            "类型" => descending ? byFolder.ThenByDescending(entry => entry.Type) : byFolder.ThenBy(entry => entry.Type),
            "大小" => descending ? byFolder.ThenByDescending(entry => entry.Length) : byFolder.ThenBy(entry => entry.Length),
            _ => descending ? byFolder.ThenByDescending(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                : byFolder.ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
        }).ToArray();
        if (preserveView)
        {
            UpdateWatchedList(displayed);
            return;
        }

        FileList.ItemsSource = displayed;
        if (selectPath is not null)
        {
            FileList.SelectedItem = FileList.Items.Cast<FileEntry>().FirstOrDefault(entry => FileOperations.SamePath(entry.FullPath, selectPath));
            if (FileList.SelectedItem is FileEntry selected) FileList.ScrollIntoView(selected);
        }
        CountSelection();
    }

    private void UpdateNavigation()
    {
        BackButton.IsEnabled = Current?.Back.Count > 0;
        ForwardButton.IsEnabled = Current?.Forward.Count > 0;
        UpButton.IsEnabled = Current is { IsDriveOverview: false };
    }

    private static string FolderLabel(string path) => path == BrowserTab.DriveOverviewPath ? "此电脑"
        : Path.GetFileName(Path.TrimEndingDirectorySeparator(path)) is { Length: > 0 } name ? name : path.TrimEnd('\\');

    private static MenuItem Menu(string name, string key, Action action, string? glyph = null, string? colorKey = null)
    {
        var item = new MenuItem { Header = name, InputGestureText = key };
        if (glyph is not null) item.Icon = MenuGlyph(glyph, colorKey);
        item.Click += (_, _) => action();
        return item;
    }

    private static TextBlock MenuGlyph(string glyph, string? colorKey = null)
    {
        var icon = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 15 };
        icon.SetResourceReference(TextBlock.ForegroundProperty, colorKey ?? "IconBlue");
        return icon;
    }


    private void AddLocation(Panel panel, Location place, bool pinned = false, string? iconColor = null)
    {
        var icon = new TextBlock
        {
            Text = place.Icon,
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            Margin = new(0, 2, 8, 0),
            VerticalAlignment = VerticalAlignment.Top
        };
        icon.SetResourceReference(TextBlock.ForegroundProperty, iconColor ?? "Accent");
        var label = new TextBlock
        {
            Text = place.Label,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontWeight = pinned ? FontWeights.SemiBold : FontWeights.Normal
        };
        var text = new StackPanel { Children = { label } };
        if (pinned)
        {
            var path = new TextBlock { Text = place.Path, TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new(0, 3, 0, 0) };
            path.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            text.Children.Add(path);
        }
        var content = new DockPanel { Children = { icon, text } };
        var button = new Button
        {
            Content = content,
            ToolTip = place.Path,
            Style = (Style)FindResource("LocationButton"),
            Background = Brushes.Transparent,
            Margin = pinned ? new Thickness(0, 0, 0, 5) : new Thickness()
        };
        if (pinned) button.SetResourceReference(Control.BackgroundProperty, panel == OverviewPinsPanel ? "OverviewTile" : "Panel");
        if (panel == OverviewPinsPanel) button.Margin = new Thickness(0, 0, 6, 6);
        button.Click += (_, _) => _ = Navigate(place.Path);
        if (pinned)
        {
            button.ContextMenu = new ContextMenu
            {
                Items =
                {
                    Menu("设置标签…", "", () =>
                    {
                        if (Prompt("设置路径标签", "标签名称", place.Label) is { } name)
                            UpdatePins(pins =>
                            {
                                var i = pins.FindIndex(pin => FileOperations.SamePath(pin.Path, place.Path));
                                if (i >= 0) pins[i] = pins[i] with { Label = name };
                            });
                    }),
                    Menu("恢复默认标签", "", () => UpdatePins(pins =>
                    {
                        var i = pins.FindIndex(pin => FileOperations.SamePath(pin.Path, place.Path));
                        if (i >= 0) pins[i] = pins[i] with { Label = FolderLabel(place.Path) };
                    })),
                    Menu("取消固定", "", () => UpdatePins(pins => pins.RemoveAll(pin => FileOperations.SamePath(pin.Path, place.Path))))
                }
            };
        }
        panel.Children.Add(button);
    }

    private void ShowPins(List<Location> pins)
    {
        PinsPanel.Children.Clear();
        OverviewPinsPanel.Children.Clear();
        if (pins.Count == 0)
        {
            var empty = new TextBlock { Text = "点 ＋ 固定当前文件夹", Margin = new(5, 5, 0, 3) };
            empty.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            PinsPanel.Children.Add(empty);
        }
        OverviewPinsSection.Visibility = pins.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        foreach (var pin in pins)
        {
            AddLocation(PinsPanel, pin, pinned: true);
            AddLocation(OverviewPinsPanel, pin, pinned: true);
        }
    }

    private void Pin(string path)
    {
        var alreadyPinned = false;
        if (UpdatePins(pins =>
        {
            alreadyPinned = pins.Any(pin => FileOperations.SamePath(pin.Path, path));
            if (!alreadyPinned) pins.Add(new(FolderLabel(path), path, "\uE718"));
        })) Report(alreadyPinned ? "该路径已固定" : "已固定到经常访问");
    }

    private bool UpdatePins(Action<List<Location>> update)
    {
        try { ShowPins(PinnedLocations.Update(update)); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Report($"无法保存经常访问（{ex.Message}）");
            return false;
        }
    }

    private void CountSelection()
    {
        if (Current?.IsDriveOverview == true)
        {
            CountText.Text = $"{DriveList.Items.Count} 个驱动器";
            return;
        }
        var count = FileList.SelectedItems.Count;
        CountText.Text = count == 0 ? $"{FileList.Items.Count} 项" : $"{FileList.Items.Count} 项 · 已选 {count} 项";
    }

    private void FileSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sortingFiles) return;
        CountSelection();
        _ = ShowPreview();
    }

    private async Task ShowPreview()
    {
        preview?.Cancel();
        previewPixels = null;
        previewNifti = null;
        ClearPreviewPixel();
        PreviewImage.Source = null;
        ResetPreviewZoom();
        if (FileList.SelectedItems.Count != 1 || FileList.SelectedItem is not FileEntry entry || !NiftiFiles.IsReviewable(entry))
        {
            SetPreviewVisible(false);
            return;
        }

        SetPreviewVisible(true);
        PreviewName.Text = entry.Name;
        PreviewDetails.Text = string.Join("  ·  ", new[] { entry.Type, entry.Size, entry.UpdatedText }.Where(value => value.Length > 0));
        PreviewHint.Text = "正在加载图片…";
        PreviewHint.Visibility = Visibility.Visible;

        using var request = new CancellationTokenSource();
        preview = request;
        try
        {
            await Task.Delay(90, request.Token);
            var review = ReviewPairs().GetValueOrDefault(entry.FullPath) ?? new ReviewItem(entry);
            var isNifti = NiftiFiles.IsNifti(review.Name);
            if (!isNifti && entry.Length > BrowserImage.MaxFileSize)
            {
                PreviewHint.Text = "图片超过 64 MB，双击查看大图";
                return;
            }
            await PreviewDecoder.WaitAsync(request.Token);
            try
            {
                var (image, pixels, nifti) = await Task.Run(() =>
                {
                    if (isNifti)
                    {
                        var loaded = NiftiFiles.Load(review, request.Token);
                        return (loaded.Preview(request.Token), loaded.Dimensions, (NiftiReview?)loaded);
                    }
                    var (normalImage, normalPixels) = BrowserImage.LoadPreview(entry.FullPath, request.Token);
                    return (normalImage, normalPixels, (NiftiReview?)null);
                }, request.Token);
                if (request.IsCancellationRequested || closing) return;
                previewPixels = pixels;
                previewNifti = nifti;
                if (nifti is not null)
                    PreviewDetails.Text = review.Mask is null ? nifti.Status :
                        $"原图 {review.Name}\n标注 {review.Mask.Name}\n{nifti.Status}";
                PreviewImage.Source = image;
                SizePreviewImage();
                PreviewHint.Visibility = Visibility.Collapsed;
                UpdatePreviewPixel();
            }
            finally
            {
                PreviewDecoder.Release();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or FormatException or NotSupportedException or ArgumentException or COMException)
        {
            if (!request.IsCancellationRequested && !closing)
                PreviewHint.Text = NiftiFiles.IsNifti(entry.Name) ? ex.Message : "无法预览，可用右键「打开方式」";
        }
        finally
        {
            if (preview == request) preview = null;
        }
    }

    private void SelectRightClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject element) return;
        if (ItemsControl.ContainerFromElement(FileList, element) is ListViewItem row)
        {
            if (!row.IsSelected) FileList.SelectedItem = row.DataContext;
            row.Focus();
        }
        else FileList.UnselectAll();
    }

    private void PrepareMenu(object sender, ContextMenuEventArgs e)
    {
        var count = FileList.SelectedItems.Count;
        colorMenu.IsEnabled = count > 0 && !working;
        menuOpen.IsEnabled = count == 1;
        menuCut.IsEnabled = count > 0;
        menuCopy.IsEnabled = count > 0;
        menuRename.IsEnabled = count == 1 && !working;
        menuDelete.IsEnabled = count > 0 && !working;
        menuPin.IsEnabled = count == 1 && FileList.SelectedItem is FileEntry { IsDirectory: true };
        menuPath.IsEnabled = count > 0;
        compressMenu.IsEnabled = count > 0 && !working;
        extractMenu.IsEnabled = !working;
        var selectedFile = count == 1 && FileList.SelectedItem is FileEntry { IsDirectory: false };
        var canExtract = selectedFile && FileList.SelectedItem is FileEntry entry && ArchiveFiles.GetExtension(entry.Name) is not null;
        ApplyMenuVisibility(selectedFile, canExtract);
    }

    private Dictionary<string, ReviewItem> ReviewPairs() =>
        NiftiFiles.Pairs((Current?.Entries ?? []).Concat(FileList.Items.OfType<FileEntry>()));

    private async Task OpenSelection()
    {
        if (FileList.SelectedItem is not FileEntry entry || FileList.SelectedItems.Count != 1) return;
        if (NiftiFiles.IsReviewable(entry))
        {
            var pairs = ReviewPairs();
            var selected = pairs.GetValueOrDefault(entry.FullPath) ?? new ReviewItem(entry);
            var images = FileList.Items.OfType<FileEntry>().Where(NiftiFiles.IsReviewable)
                .Select(item => pairs.GetValueOrDefault(item.FullPath) ?? new ReviewItem(item))
                .DistinctBy(item => item.FullPath, StringComparer.OrdinalIgnoreCase).ToArray();
            var viewer = new ImageViewerWindow(images, Array.FindIndex(images,
                item => StringComparer.OrdinalIgnoreCase.Equals(item.FullPath, selected.FullPath)))
            {
                Owner = this, Icon = Icon
            };
            viewer.Resources[typeof(Button)] = FindResource(typeof(Button));
            (Application.Current as App)?.TrackWindowTheme(viewer);
            viewer.Show();
            return;
        }
        if (entry.IsDirectory)
        {
            await Navigate(entry.FullPath);
            return;
        }

        if (entry.IsShortcut)
        {
            var tab = Current;
            var folder = await Task.Run(() => WindowsIntegration.ReadShortcutFolder(entry.FullPath));
            if (closing || Current != tab || FileList.SelectedItems.Count != 1 || FileList.SelectedItem != entry) return;
            if (folder is not null)
            {
                await Navigate(folder);
                return;
            }
        }

        ShellStart(new ProcessStartInfo(entry.FullPath) { UseShellExecute = true });
    }

    private void OpenExplorer()
    {
        if (Current is not { Loaded: true } tab) return;
        var explorer = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
        if (FileList.SelectedItem is FileEntry entry && FileList.SelectedItems.Count == 1)
            explorer.Arguments = $"/select,\"{entry.FullPath}\"";
        else explorer.ArgumentList.Add(tab.Path);
        ShellStart(explorer);
    }

    private void ShowProperties()
    {
        if (Current is not { Loaded: true } tab) return;
        var path = FileList.SelectedItems.Count == 1 && FileList.SelectedItem is FileEntry entry ? entry.FullPath : tab.Path;
        var info = new ShellExecuteInfo
        {
            Size = Marshal.SizeOf<ShellExecuteInfo>(), Mask = 0xC, Window = new System.Windows.Interop.WindowInteropHelper(this).Handle,
            Verb = "properties", File = path, Show = 1
        };
        if (!ShellExecuteEx(ref info)) Report($"无法显示属性（{new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message}）");
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellExecuteInfo
    {
        public int Size;
        public uint Mask;
        public IntPtr Window;
        public string Verb, File;
        public IntPtr Parameters, Directory;
        public int Show;
        public IntPtr Instance, IdList, Class, ClassKey;
        public uint HotKey;
        public IntPtr Icon, Process;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellExecuteEx(ref ShellExecuteInfo info);

    private void ShellStart(ProcessStartInfo start)
    {
        try { using var process = Process.Start(start); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            Report($"无法打开（{ex.Message}）");
        }
    }

    private void CopyToClipboard(bool move)
    {
        var paths = Selection.Select(entry => entry.FullPath).ToArray();
        if (paths.Length == 0) return;
        try
        {
            FileClipboard.Write(paths, move);
            Report(move ? $"已剪切 {paths.Length} 项" : $"已复制 {paths.Length} 项到剪贴板");
        }
        catch (ExternalException) { Report("剪贴板暂时被占用，请再试一次。"); }
    }

    private void CopyPaths()
    {
        var paths = Selection.Select(entry => entry.FullPath).ToArray();
        if (paths.Length == 0) return;
        try { Clipboard.SetText(string.Join(Environment.NewLine, paths)); Report("已复制路径"); }
        catch (ExternalException) { Report("剪贴板暂时被占用，请再试一次。"); }
    }

    private async Task Paste()
    {
        if (working || Current is not { Loaded: true }) return;
        ClipboardFiles? files;
        try { files = FileClipboard.Read(); }
        catch (ExternalException) { Report("剪贴板暂时被占用，请再试一次。"); return; }
        if (files is null) { Report("剪贴板里没有文件"); return; }
        await Transfer(files.Paths, files.Move, files);
    }

    private async Task Transfer(string[] paths, bool move, ClipboardFiles? clipboard = null)
    {
        if (working || Current is not { Loaded: true } tab) return;
        var destination = tab.Path;
        await Perform(move ? "正在移动…" : "正在复制…", () => FileOperations.Paste(paths, destination, move), tab, result =>
        {
            if (clipboard is not null)
            {
                try { FileClipboard.CompleteMove(clipboard, result); }
                catch (ExternalException) { }
            }
            var message = move ? $"已移动 {result.Completed} 项" : $"已复制 {result.Completed} 项";
            if (result.Moves is { Count: > 0 } moves && !MoveColors(moves.Select(pair => (pair.Key, (string?)pair.Value))))
                message += " · 颜色标签未迁移";
            return message;
        });
    }


    private async Task Recycle()
    {
        if (working || Current is not { Loaded: true } tab) return;
        var paths = Selection.Select(entry => entry.FullPath).ToArray();
        if (paths.Length == 0) return;
        if (paths.Any(path => Path.GetPathRoot(path) is not { } root || root.StartsWith(@"\\") ||
            new DriveInfo(root).DriveType != DriveType.Fixed))
        {
            Report("网络位置或可移动驱动器上的文件，请在 Windows 资源管理器中处理。");
            return;
        }
        if (!PathDeckDialogs.Confirm(this, "移到回收站？", $"将选中的 {paths.Length} 项移到回收站。", "移到回收站", "取消")) return;
        await Perform("正在移到回收站…", () => FileOperations.Recycle(paths), tab, result =>
        {
            var cleared = MoveColors(paths.Except(result.Remaining, StringComparer.OrdinalIgnoreCase).Select(path => (path, (string?)null)));
            return $"已移到回收站 {result.Completed} 项" + (cleared ? "" : " · 颜色标签未清除");
        });
    }

    private async Task Perform(string progress, Func<OperationResult> action, BrowserTab tab, Func<OperationResult, string> complete)
    {
        working = true;
        Report(progress);
        var sourcePath = tab.Path;
        var taskbar = TaskbarItemInfo ??= new TaskbarItemInfo();
        taskbar.ProgressState = TaskbarItemProgressState.Indeterminate;
        try
        {
            var result = await Task.Run(action);
            var message = complete(result);
            if (Current == tab && FileOperations.SamePath(tab.Path, sourcePath) && listing is null)
                await Navigate(tab.Path, Navigation.Refresh, search);
            Report(result.Errors.Length == 0 ? message : $"{message}，{result.Errors.Length} 项未完成 · {result.Errors[0]}");
        }
        catch (OperationCanceledException)
        {
            Report("操作已取消");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            Report($"操作未完成（{ex.Message}）");
        }
        finally
        {
            working = false;
            taskbar.ProgressState = TaskbarItemProgressState.None;
        }
    }

    private async Task Rename()
    {
        if (working || Current is not { Loaded: true } tab || FileList.SelectedItems.Count != 1 || FileList.SelectedItem is not FileEntry entry)
            return;
        if (Prompt("重命名", "名称", entry.Name) is not { } name || name == entry.Name) return;
        await RenameEntry(tab, entry, name);
    }

    private async Task RenameEntry(BrowserTab tab, FileEntry entry, string name)
    {
        working = true;
        try
        {
            FileOperations.ValidateName(name);
            var newPath = Path.Combine(Path.GetDirectoryName(entry.FullPath)!, name);
            await Task.Run(() =>
            {
                if (entry.IsDirectory) Directory.Move(entry.FullPath, newPath);
                else File.Move(entry.FullPath, newPath);
            });
            var movedTag = MoveColors([(entry.FullPath, newPath)]);
            if (Current == tab && listing is null) await Navigate(tab.Path, Navigation.Refresh, search, newPath);
            Report(movedTag ? "已重命名" : "已重命名 · 颜色标签未迁移");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { Report($"无法重命名（{ex.Message}）"); }
        finally { working = false; }
    }

    private async Task CreateItem(bool folder)
    {
        if (working || Current is not { Loaded: true } tab) return;
        var defaultName = folder ? "新建文件夹" : "新建文本文档.txt";
        if (Prompt(folder ? "新建文件夹" : "新建文本文档", "名称", defaultName) is not { } name) return;
        working = true;
        try
        {
            FileOperations.ValidateName(name);
            var path = tab.Path;
            var created = await Task.Run(() =>
            {
                var target = FileOperations.AvailableName(path, name, folder);
                if (folder) Directory.CreateDirectory(target);
                else { using var file = new FileStream(target, FileMode.CreateNew); }
                return target;
            });
            if (Current == tab && FileOperations.SamePath(tab.Path, path) && listing is null)
                await Navigate(path, Navigation.Refresh, search, created);
            Report($"已新建 {Path.GetFileName(created)}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { Report($"无法新建（{ex.Message}）"); }
        finally { working = false; }
    }

    private string? Prompt(string title, string label, string value)
    {
        var dialog = PathDeckDialogs.Create(this, title, 390);
        var input = new TextBox { Text = value, Margin = new(0, 6, 0, 0), Style = (Style)FindResource("DialogInput") };
        var card = new Border
        {
            Style = (Style)FindResource("DialogCard"),
            Child = new StackPanel { Children = { new TextBlock { Text = label, FontWeight = FontWeights.SemiBold }, input } }
        };
        var ok = new Button { Content = "确定", IsDefault = true, MinWidth = 68, Style = (Style)FindResource("DialogPrimaryButton") };
        var cancel = new Button { Content = "取消", IsCancel = true, MinWidth = 68, Margin = new(0, 0, 8, 0), Style = (Style)FindResource("DialogButton") };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new(0, 14, 0, 0), Children = { cancel, ok }
        };
        dialog.Content = new StackPanel { Margin = new(16), Children = { card, buttons } };
        ok.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(input.Text)) dialog.DialogResult = true; };
        dialog.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        return dialog.ShowDialog() == true ? input.Text : null;
    }

    private void Report(string message) => StatusText.Text = message;

    private void BeginDrag(object sender, MouseButtonEventArgs e)
    {
        dragStart = null;
        dragPaths = null;
        if (e.OriginalSource is not DependencyObject element || ItemsControl.ContainerFromElement(FileList, element) is not ListViewItem { DataContext: FileEntry entry } row)
            return;
        dragStart = e.GetPosition(FileList);
        dragPaths = row.IsSelected && Keyboard.Modifiers == ModifierKeys.None ? Selection.Select(item => item.FullPath).ToArray() : [entry.FullPath];
    }

    private void DragFiles(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || dragStart is not Point origin || dragPaths is not { Length: > 0 } paths)
            return;
        var position = e.GetPosition(FileList);
        if (Math.Abs(position.X - origin.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(position.Y - origin.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        dragStart = null;
        DragDrop.DoDragDrop(FileList, new DataObject(DataFormats.FileDrop, paths), DragDropEffects.Copy);
    }

    private void FileDragOver(object sender, DragEventArgs e)
    {
        e.Effects = !working && Current?.Loaded == true && e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void FileDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || Current is not { } tab) return;
        e.Handled = true;
        if (paths.All(path => FileOperations.SamePath(Path.GetDirectoryName(path) ?? "", tab.Path))) return;
        await Transfer(paths, false);
    }

    private async Task GoBack()
    {
        if (Current is { } tab && tab.Back.Count > 0) await Navigate(tab.Back[^1], Navigation.Back);
    }

    private async Task GoForward()
    {
        if (Current is { } tab && tab.Forward.Count > 0) await Navigate(tab.Forward[^1], Navigation.Forward);
    }

    private async Task GoUp()
    {
        if (Current is { IsDriveOverview: false } tab)
            await Navigate(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(tab.Path)) ?? BrowserTab.DriveOverviewPath);
    }

    private Task Refresh()
    {
        if (Current is not { } tab) return Task.CompletedTask;
        if (!tab.IsDriveOverview) ShowDrives(DriveInfo.GetDrives());
        return Navigate(tab.Path, Navigation.Refresh, search);
    }
    private Task Search() => Current is { IsDriveOverview: false } tab
        ? Navigate(tab.Path, Navigation.Search, string.IsNullOrWhiteSpace(SearchBox.Text) ? null : SearchBox.Text.Trim()) : Task.CompletedTask;

    private void Shortcuts(object sender, KeyEventArgs e)
    {
        if (inlineRename is { } active && active.Editor.IsKeyboardFocusWithin) return;
        var mods = Keyboard.Modifiers;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (QuickSearchActive)
        {
            if (mods == ModifierKeys.Control && key == Key.F) { QuickQuery.Focus(); QuickQuery.SelectAll(); }
            else if (mods == ModifierKeys.Control && key == Key.T) NewTabClick(sender, e);
            else if (mods == ModifierKeys.Control && key == Key.W && Tabs.SelectedItem is TabItem quick) CloseTab(quick);
            else if (mods == ModifierKeys.None && key == Key.F5) _ = SearchQuickIndex(refreshScope: true);
            else return;
            e.Handled = true;
            return;
        }
        if (mods == ModifierKeys.Control)
        {
            if (key == Key.L) BeginAddressEdit();
            else if (key == Key.F) { SearchBox.Focus(); SearchBox.SelectAll(); }
            else if (key == Key.T) NewTabClick(sender, e);
            else if (key == Key.W && Tabs.SelectedItem is TabItem tab) CloseTab(tab);
            else if (e.OriginalSource is TextBox) return;
            else if (key == Key.C) CopyToClipboard(false);
            else if (key == Key.X) CopyToClipboard(true);
            else if (key == Key.V) _ = Paste();
            else if (key == Key.A) FileList.SelectAll();
            else return;
        }
        else if (mods == (ModifierKeys.Control | ModifierKeys.Shift) && key == Key.N) _ = CreateItem(true);
        else if (mods == (ModifierKeys.Control | ModifierKeys.Shift) && key == Key.C && e.OriginalSource is not TextBox) CopyPaths();
        else if (mods == ModifierKeys.Alt)
        {
            if (key == Key.D) BeginAddressEdit();
            else if (key == Key.Left) _ = GoBack();
            else if (key == Key.Right) _ = GoForward();
            else if (key == Key.Up) _ = GoUp();
            else if (key == Key.Enter) ShowProperties();
            else return;
        }
        else if (mods == ModifierKeys.None && key == Key.Back)
        {
            if (AddressBox.IsKeyboardFocusWithin || SearchBox.IsKeyboardFocusWithin || e.OriginalSource is TextBox) return;
            _ = GoUp();
        }
        else if (mods == ModifierKeys.None && key == Key.F5) _ = Refresh();
        else if (mods == ModifierKeys.None && e.OriginalSource is not TextBox)
        {
            if (key == Key.F2) _ = Rename();
            else if (key == Key.Delete) _ = Recycle();
            else if (key == Key.Enter && FileList.IsKeyboardFocusWithin) _ = OpenSelection();
            else if (key == Key.Enter && DriveList.IsKeyboardFocusWithin && DriveList.SelectedItem is DriveEntry drive) _ = Navigate(drive.Path);
            else return;
        }
        else return;
        e.Handled = true;
    }

    private async void SearchKey(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Escape)) return;
        e.Handled = true;
        if (e.Key == Key.Escape) SearchBox.Clear();
        await Search();
    }

    private void NewTabClick(object sender, RoutedEventArgs e) => AddTab(BrowserTab.DriveOverviewPath);
    private void PinCurrentClick(object sender, RoutedEventArgs e) { if (Current is { Loaded: true } tab) Pin(tab.Path); }
    private async void BackClick(object sender, RoutedEventArgs e) => await GoBack();
    private async void ForwardClick(object sender, RoutedEventArgs e) => await GoForward();
    private async void UpClick(object sender, RoutedEventArgs e) => await GoUp();
    private async void RefreshClick(object sender, RoutedEventArgs e) => await Refresh();
    private async void SearchClick(object sender, RoutedEventArgs e) => await Search();
    private async void NewFolderClick(object sender, RoutedEventArgs e) => await CreateItem(true);
    private void CopyClick(object sender, RoutedEventArgs e) => CopyToClipboard(false);
    private async void PasteClick(object sender, RoutedEventArgs e) => await Paste();
    private void ExplorerClick(object sender, RoutedEventArgs e) => OpenExplorer();
    private async void HiddenClick(object sender, RoutedEventArgs e) => await Refresh();

    private async void OpenDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (inlineRename is not null) return;
        if (e.ChangedButton == MouseButton.Left && e.OriginalSource is DependencyObject element &&
            ItemsControl.ContainerFromElement(FileList, element) is ListViewItem)
            await OpenSelection();
    }
}

