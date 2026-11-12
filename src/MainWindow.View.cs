using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace QuickFileDesk;

public partial class MainWindow
{
    public const string ModifiedAgeHeader = "修改于";

    public static readonly DependencyProperty FileAgeNowProperty = DependencyProperty.Register(
        nameof(FileAgeNow), typeof(DateTime), typeof(MainWindow));

    public DateTime FileAgeNow => (DateTime)GetValue(FileAgeNowProperty);

    private readonly DispatcherTimer fileAgeTimer = new(DispatcherPriority.Background)
    {
        Interval = TimeSpan.FromSeconds(5)
    };

    private void UpdateFileAge() => SetValue(FileAgeNowProperty, DateTime.UtcNow);

    private enum FileViewMode { Details, Names, Thumbnails }

    private static readonly (FileViewMode Mode, string Label)[] ViewModes =
    [
        (FileViewMode.Details, "详细信息"), (FileViewMode.Names, "仅名称"), (FileViewMode.Thumbnails, "缩略图")
    ];
    private static readonly (double Size, string Label)[] IconSizes = [(16, "小"), (26, "中"), (48, "大")];

    private GridViewColumn[]? fileColumns;
    private FileViewMode fileView;
    private double iconSize = 26;
    private double detailsNameWidth;
    private bool foldersFirst = true;
    private bool sortingFiles;

    private void SortMenuClick(object sender, RoutedEventArgs e)
    {
        var menu = ToolbarMenu((Button)sender);
        foreach (var field in new[] { "名称", "修改日期", ModifiedAgeHeader, "类型", "大小" })
            menu.Items.Add(CheckedChoice(field, sortBy == field, () => SetSort(field, descending)));

        menu.Items.Add(new Separator());
        menu.Items.Add(CheckedChoice("升序", !descending, () => SetSort(sortBy, false)));
        menu.Items.Add(CheckedChoice("降序", descending, () => SetSort(sortBy, true)));
        menu.Items.Add(new Separator());
        menu.Items.Add(CheckedChoice("文件夹在前", foldersFirst, () =>
        {
            foldersFirst = !foldersFirst;
            SortVisibleFiles();
        }));
        menu.IsOpen = true;
    }

    private void ViewMenuClick(object sender, RoutedEventArgs e)
    {
        var menu = ToolbarMenu((Button)sender);
        foreach (var (mode, label) in ViewModes)
            menu.Items.Add(CheckedChoice(label, fileView == mode, () => SetFileView(mode)));
        menu.Items.Add(new Separator());
        foreach (var (size, label) in IconSizes)
            menu.Items.Add(CheckedChoice($"{label}图标", iconSize == size, () => SetIconSize(size)));
        menu.IsOpen = true;
    }

    private ContextMenu ToolbarMenu(Button button) => new()
    {
        PlacementTarget = button,
        Placement = PlacementMode.Bottom
    };

    private static MenuItem CheckedChoice(string label, bool selected, Action action)
    {
        var item = Menu(label, "", action);
        item.IsCheckable = true;
        item.IsChecked = selected;
        return item;
    }

    private void SetSort(string field, bool reverse)
    {
        sortBy = field;
        descending = reverse;
        SortVisibleFiles();
    }

    private void SortFiles(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is GridViewColumnHeader { Column.Header: string column })
            SetSort(column, column == sortBy && !descending);
    }

    private void SortVisibleFiles()
    {
        if (FileList.ItemsSource is not FileEntry[] entries) return;
        var selected = Selection;
        sortingFiles = true;
        try
        {
            DisplayEntries(entries);
            foreach (var entry in selected) FileList.SelectedItems.Add(entry);
            if (FileList.SelectedItem is FileEntry first) FileList.ScrollIntoView(first);
        }
        finally
        {
            sortingFiles = false;
        }
        CountSelection();
    }

    private void SetFileView(FileViewMode mode)
    {
        if (fileView == mode || FileList.View is not GridView grid) return;
        fileColumns ??= grid.Columns.ToArray();
        if (mode == FileViewMode.Names)
        {
            detailsNameWidth = fileColumns[0].Width;
            foreach (var column in fileColumns.Skip(1)) grid.Columns.Remove(column);
        }
        else if (fileView == FileViewMode.Names)
        {
            fileColumns[0].Width = detailsNameWidth;
            foreach (var column in fileColumns.Skip(1)) grid.Columns.Add(column);
        }
        fileView = mode;
        Resources["ShowFileThumbnails"] = mode == FileViewMode.Thumbnails;
        FitNameColumn();
    }

    private void FileListResized(object sender, SizeChangedEventArgs e) => FitNameColumn();

    private void FitNameColumn()
    {
        if (fileView == FileViewMode.Names && fileColumns is not null)
            fileColumns[0].Width = Math.Max(0, FileList.ActualWidth - SystemParameters.VerticalScrollBarWidth - 6);
    }

    private void SetIconSize(double size)
    {
        iconSize = size;
        Resources["FileIconSize"] = size;
        Resources["FileRowHeight"] = size + 10;
    }
}

