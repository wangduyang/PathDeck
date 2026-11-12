using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace QuickFileDesk;

public partial class MainWindow
{
    private readonly List<(Location Location, StackPanel Visual, double Width)> breadcrumbs = [];
    private string? breadcrumbPath;
    private ContextMenu? addressMenu;

    private void UpdateAddress(string path)
    {
        AddressBox.Text = path;
        if (string.Equals(breadcrumbPath, path, StringComparison.Ordinal)) return;
        if (addressMenu is not null) addressMenu.IsOpen = false;
        breadcrumbPath = path;
        breadcrumbs.Clear();
        BreadcrumbItems.Children.Clear();

        if (path == BrowserTab.DriveOverviewPath)
        {
            BreadcrumbMore.Visibility = Visibility.Collapsed;
            BreadcrumbItems.Children.Add(new TextBlock
            {
                Text = "此电脑", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 3, 8, 3)
            });
            return;
        }

        var root = Path.GetPathRoot(path);
        if (root is null) return;
        var location = root;
        AddBreadcrumb(new(root.TrimEnd('\\'), root), path);
        foreach (var name in path[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            location = Path.Combine(location, name);
            AddBreadcrumb(new(name, location), path);
        }
        FitBreadcrumbs();
    }

    private void AddBreadcrumb(Location location, string currentPath)
    {
        var label = new TextBlock { Text = location.Label, MaxWidth = 145, TextTrimming = TextTrimming.CharacterEllipsis };
        if (FileOperations.SamePath(location.Path, currentPath))
            label.FontWeight = FontWeights.SemiBold;
        var link = new Button { Content = label, Style = (Style)FindResource("PathButton"), ToolTip = location.Path };
        AutomationProperties.SetName(link, location.Label);
        link.Click += async (_, _) =>
        {
            FileList.Focus();
            await Navigate(location.Path);
        };
        var arrow = new Button
        {
            Content = "\uE76C", Width = 19, Style = (Style)FindResource("PathArrow"),
            ToolTip = $"浏览「{location.Label}」下的文件夹"
        };
        AutomationProperties.SetName(arrow, $"浏览 {location.Label} 下的文件夹");
        arrow.Click += async (_, _) => await OpenBreadcrumbFolders(arrow, location);

        var group = new StackPanel { Orientation = Orientation.Horizontal, Children = { link, arrow } };
        BreadcrumbItems.Children.Add(group);
        group.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        breadcrumbs.Add((location, group, group.DesiredSize.Width));
    }

    private void FitBreadcrumbs()
    {
        if (breadcrumbs.Count == 0 || BreadcrumbViewport.ActualWidth <= 0) return;
        var remaining = breadcrumbs.Sum(part => part.Width);
        var hidden = 0;
        if (remaining > BreadcrumbViewport.ActualWidth)
        {
            BreadcrumbMore.Visibility = Visibility.Visible;
            BreadcrumbMore.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var available = BreadcrumbViewport.ActualWidth - BreadcrumbMore.DesiredSize.Width;
            while (remaining > available && hidden < breadcrumbs.Count - 1)
                remaining -= breadcrumbs[hidden++].Width;
        }
        BreadcrumbMore.Visibility = hidden == 0 ? Visibility.Collapsed : Visibility.Visible;
        for (var i = 0; i < breadcrumbs.Count; i++)
            breadcrumbs[i].Visual.Visibility = i < hidden ? Visibility.Collapsed : Visibility.Visible;
    }

    private void BreadcrumbSizeChanged(object sender, SizeChangedEventArgs e) => FitBreadcrumbs();

    private void OpenHiddenBreadcrumbs(object sender, RoutedEventArgs e)
    {
        var menu = CreateAddressMenu((Button)sender);
        foreach (var part in breadcrumbs.Where(part => part.Visual.Visibility == Visibility.Collapsed))
            menu.Items.Add(BreadcrumbMenuItem(part.Location.Label, part.Location.Path));
        menu.IsOpen = true;
    }

    private async Task OpenBreadcrumbFolders(Button source, Location parent)
    {
        const int maxMenuFolders = 200;
        var menu = CreateAddressMenu(source);
        menu.Items.Add(new MenuItem { Header = "正在读取文件夹…", IsEnabled = false });
        menu.IsOpen = true;
        var options = new EnumerationOptions
        {
            AttributesToSkip = HiddenToggle.IsChecked == true ? 0 : FileAttributes.Hidden | FileAttributes.System,
            IgnoreInaccessible = false
        };
        try
        {
            var paths = await Task.Run(() => Directory.EnumerateDirectories(parent.Path, "*", options)
                .Take(maxMenuFolders + 1).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).ToArray());
            if (!menu.IsOpen) return;
            menu.Items.Clear();
            foreach (var path in paths.Take(maxMenuFolders))
                menu.Items.Add(BreadcrumbMenuItem(Path.GetFileName(path), path));
            if (paths.Length == 0)
                menu.Items.Add(new MenuItem { Header = "没有子文件夹", IsEnabled = false });
            if (paths.Length > maxMenuFolders)
                menu.Items.Add(BreadcrumbMenuItem("在文件列表中查看全部", parent.Path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            if (!menu.IsOpen) return;
            menu.Items.Clear();
            menu.Items.Add(new MenuItem { Header = "无法读取文件夹", IsEnabled = false, ToolTip = ex.Message });
        }
    }

    private ContextMenu CreateAddressMenu(Button source)
    {
        if (addressMenu is not null) addressMenu.IsOpen = false;
        var menu = new ContextMenu
        {
            PlacementTarget = source,
            Placement = PlacementMode.Bottom,
            MaxHeight = Math.Min(440, SystemParameters.WorkArea.Height * .65)
        };
        addressMenu = menu;
        menu.Closed += (_, _) => { if (addressMenu == menu) addressMenu = null; };
        return menu;
    }

    private MenuItem BreadcrumbMenuItem(string label, string path)
    {
        var item = Menu(label, "", () =>
        {
            FileList.Focus();
            _ = Navigate(path);
        });
        item.ToolTip = path;
        return item;
    }

    private void BeginAddressEdit()
    {
        AddressBox.Text = Current?.Path ?? "";
        AddressTrail.Visibility = Visibility.Collapsed;
        AddressBox.Visibility = Visibility.Visible;
        AddressBox.Focus();
        AddressBox.SelectAll();
    }

    private void EndAddressEdit()
    {
        if (AddressBox.Visibility != Visibility.Visible) return;
        AddressBox.Visibility = Visibility.Collapsed;
        AddressBox.Text = Current?.Path ?? "";
        AddressTrail.Visibility = Visibility.Visible;
    }

    private void EditAddressClick(object sender, RoutedEventArgs e) => BeginAddressEdit();
    private void AddressLostFocus(object sender, KeyboardFocusChangedEventArgs e) => EndAddressEdit();

    private async void AddressKey(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Escape)) return;
        e.Handled = true;
        var path = AddressBox.Text;
        EndAddressEdit();
        FocusLocation();
        if (e.Key != Key.Enter || string.IsNullOrWhiteSpace(path)) return;

        var input = path.Trim();
        if (input.Equals("cmd", StringComparison.OrdinalIgnoreCase) ||
            input.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase))
        {
            OpenCommandPrompt();
            return;
        }

        await Navigate(path);
    }

    private void OpenCommandPrompt()
    {
        if (Current is not { Loaded: true } tab)
        {
            Report("请先打开一个文件夹。");
            return;
        }

        ShellStart(CreateCommandPromptStartInfo(tab.Path));
    }

    private static ProcessStartInfo CreateCommandPromptStartInfo(string directory)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
        {
            WorkingDirectory = directory,
            Arguments = "/d",
            UseShellExecute = true
        };
        if (directory.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            directory = @"\\" + directory[8..];

        if (directory.StartsWith(@"\\", StringComparison.Ordinal) &&
            !directory.StartsWith(@"\\?\", StringComparison.Ordinal) &&
            !directory.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            const string variable = "ONECOMMANDER_CMD_DIRECTORY";
            start.UseShellExecute = false;
            start.WorkingDirectory = Environment.SystemDirectory;
            start.Environment[variable] = directory;
            start.Arguments = $"/d /e:on /v:off /k pushd \"%{variable}%\"";
        }

        return start;
    }
}

