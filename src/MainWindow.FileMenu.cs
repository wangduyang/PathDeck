using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace QuickFileDesk;

public partial class MainWindow
{
    private const string MenuSettingsFile = "menu.json";
    private HashSet<string> hiddenMenuItems = new(StringComparer.Ordinal);
    private MenuItem compressMenu = null!;
    private MenuItem extractMenu = null!;
    private MenuItem openWithMenu = null!;
    private CancellationTokenSource? archiveCancellation;

    private void LoadFileMenu()
    {
        try { hiddenMenuItems = ReadHiddenMenuItems().ToHashSet(StringComparer.Ordinal); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Report($"无法加载右键菜单设置（{ex.Message}）");
        }
    }

    private static List<string> ReadHiddenMenuItems() => PortableSettings.Load<List<string>>(MenuSettingsFile) ?? [];

    private void ApplyMenuVisibility(bool canOpenWith, bool canExtract)
    {
        Separator? nextSeparator = null;
        var hasVisibleItem = false;
        foreach (var item in FileList.ContextMenu.Items)
        {
            if (item is Separator separator)
            {
                separator.Visibility = Visibility.Collapsed;
                if (hasVisibleItem) nextSeparator = separator;
                continue;
            }
            if (item is not MenuItem command) continue;
            var visible = !hiddenMenuItems.Contains((string)command.Header) &&
                          (command != openWithMenu || canOpenWith) &&
                          (command != extractMenu || canExtract);
            command.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (!visible) continue;
            if (nextSeparator is not null) nextSeparator.Visibility = Visibility.Visible;
            nextSeparator = null;
            hasVisibleItem = true;
        }
    }

    internal void ConfigureFileMenu(Window owner)
    {
        var items = FileList.ContextMenu.Items.OfType<MenuItem>().Select(item => (string)item.Header).ToArray();
        var list = new StackPanel();
        var choices = items.Select(label => new CheckBox
        {
            Content = label, IsChecked = !hiddenMenuItems.Contains(label), Margin = new Thickness(5, 5, 5, 5)
        }).ToArray();
        foreach (var choice in choices) list.Children.Add(choice);

        var dialog = new Window
        {
            Title = "自定义右键菜单", Owner = owner, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
            Width = 380, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Background, Foreground = Foreground, FontFamily = FontFamily, FontSize = FontSize, Icon = Icon
        };
        (Application.Current as App)?.TrackWindowTheme(dialog);
        dialog.Resources[typeof(Button)] = FindResource(typeof(Button));
        var reset = new Button { Content = "恢复默认", Margin = new Thickness(0, 0, 8, 0) };
        var save = new Button { Content = "保存", IsDefault = true, MinWidth = 64, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "取消", IsCancel = true, MinWidth = 64 };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { reset, save, cancel }
        };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
        error.SetResourceReference(TextBlock.ForegroundProperty, "ErrorText");
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(18),
            Children =
            {
                new TextBlock { Text = "勾选要在文件列表右键菜单中显示的项目。", TextWrapping = TextWrapping.Wrap },
                new ScrollViewer { Content = list, MaxHeight = 410, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 10, 0, 10) },
                error, buttons
            }
        };
        reset.Click += (_, _) => { foreach (var choice in choices) choice.IsChecked = true; };
        save.Click += (_, _) =>
        {
            try
            {
                hiddenMenuItems = PortableSettings.Update(MenuSettingsFile, @"Local\1Commander.FileMenu", ReadHiddenMenuItems, saved =>
                {
                    saved.Clear();
                    saved.AddRange(choices.Where(choice => choice.IsChecked != true).Select(choice => (string)choice.Content));
                }).ToHashSet(StringComparer.Ordinal);
                dialog.DialogResult = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                error.Text = $"无法保存菜单设置（{ex.Message}）";
            }
        };
        dialog.ShowDialog();
    }

    private MenuItem CreateCompressMenu()
    {
        var menu = new MenuItem { Header = "压缩为", Icon = MenuGlyph("\uE7B8", "IconGold") };
        foreach (var extension in new[] { ".zip", ".7z", ".rar", ".tar", ".tar.gz", ".tar.bz2", ".tar.xz" })
        {
            if (extension == ".rar" && !ArchiveFiles.CanCreateRar) continue;
            menu.Items.Add(Menu(extension[1..].ToUpperInvariant() + "…", "", () => _ = CompressSelection(extension), "\uE7B8", "IconGold"));
        }
        return menu;
    }

    private async Task CompressSelection(string extension)
    {
        if (working || Current is not { Loaded: true } tab) return;
        var items = Selection;
        if (items.Length == 0) return;
        var stem = items.Length == 1
            ? (items[0].IsDirectory ? items[0].Name : Path.GetFileNameWithoutExtension(items[0].Name)) : "归档";
        if (string.IsNullOrWhiteSpace(stem)) stem = "归档";
        var name = Prompt("压缩为 " + extension[1..].ToUpperInvariant(), "压缩包名称", stem + extension);
        if (name is null) return;
        if (!name.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) name += extension;
        var directory = tab.Path;
        var paths = items.Select(entry => entry.FullPath).ToArray();
        await RunArchive(tab, "正在压缩…", (token, progress) => ArchiveFiles.Compress(directory, paths, name, extension, token, progress));
    }

    private async Task ExtractSelection()
    {
        if (working || Current is not { Loaded: true } tab || FileList.SelectedItems.Count != 1 ||
            FileList.SelectedItem is not FileEntry { IsDirectory: false } entry || ArchiveFiles.GetExtension(entry.Name) is null)
            return;
        await RunArchive(tab, "正在解压…", (token, progress) => ArchiveFiles.Extract(entry.FullPath, token, progress));
    }

    private async Task RunArchive(BrowserTab tab, string status,
        Func<CancellationToken, IProgress<ArchiveProgress>, string> operation)
    {
        using var request = new CancellationTokenSource();
        archiveCancellation = request;
        ArchiveProgressPanel.Visibility = Visibility.Visible;
        ArchiveProgressBar.IsIndeterminate = true;
        CancelArchiveButton.IsEnabled = true;

        var progress = new Progress<ArchiveProgress>(value =>
        {
            if (archiveCancellation != request || request.IsCancellationRequested) return;
            if (value.WrittenBytes is > 0)
                Report($"{status} 已写入 {value.WrittenBytes.Value / (1024.0 * 1024):N1} MB");
            else if (value.StartedEntries is > 0)
                Report($"{status} 正在处理第 {value.StartedEntries.Value:N0} 项");
        });

        try
        {
            var output = "";
            await Perform(status, () =>
            {
                output = Path.GetFileName(operation(request.Token, progress));
                return new OperationResult(1, [], []);
            }, tab, _ => $"已生成 {output}");
        }
        finally
        {
            archiveCancellation = null;
            ArchiveProgressBar.IsIndeterminate = false;
            ArchiveProgressPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void CancelArchiveClick(object sender, RoutedEventArgs e)
    {
        if (archiveCancellation is not { } request || request.IsCancellationRequested) return;
        CancelArchiveButton.IsEnabled = false;
        Report("正在取消…");
        request.Cancel();
    }

    private void OpenWithSelection()
    {
        if (FileList.SelectedItems.Count != 1 || FileList.SelectedItem is not FileEntry { IsDirectory: false } entry) return;
        if (WindowsIntegration.OpenWith(this, entry.FullPath) is string error) Report(error);
    }
}

