using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace QuickFileDesk;

public partial class MainWindow
{
    private DirectoryWatch? directoryWatch;
    private readonly DispatcherTimer liveRefreshTimer = new(DispatcherPriority.Background)
    {
        Interval = TimeSpan.FromMilliseconds(300)
    };

    private void SetDirectoryWatch(DirectoryWatch? next)
    {
        if (directoryWatch == next) return;
        directoryWatch?.Dispose();
        directoryWatch = next;
    }

    private async Task RefreshChangedDirectory()
    {
        if (closing || working || inlineRename is not null || listing is not null ||
            Current is not { Loaded: true } tab || directoryWatch is not { } watch ||
            !watch.Matches(tab.Path, search is not null) || watch.TakeChanges() is not { } changes)
            return;

        var query = search;
        using var request = new CancellationTokenSource();
        listing = request;
        try
        {
            var entries = await Task.Run(() => BrowserData.List(watch.Path, query, request.Token), request.Token);
            if (request.IsCancellationRequested || closing || Current != tab || directoryWatch != watch) return;
            if (working || inlineRename is not null)
            {
                watch.RetryLater();
                return;
            }

            var previous = query is null ? tab.Entries : FileList.ItemsSource as FileEntry[] ?? [];
            ReuseUnchangedEntries(entries, previous, changes);
            if (query is null) tab.Entries = entries;
            DisplayEntries(entries, preserveView: true);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            if (!request.IsCancellationRequested) watch.RetryLater();
        }
        finally
        {
            if (request.IsCancellationRequested && !closing && directoryWatch == watch) watch.RetryLater();
            if (listing == request) listing = null;
        }
    }

    private static void ReuseUnchangedEntries(FileEntry[] entries, FileEntry[] previous, DirectoryChanges changes)
    {
        var byPath = previous.ToDictionary(entry => entry.FullPath, StringComparer.Ordinal);
        for (var i = 0; i < entries.Length; i++)
        {
            var entry = entries[i];
            if (!changes.Contains(entry.FullPath) && byPath.TryGetValue(entry.FullPath, out var old) &&
                old.IsDirectory == entry.IsDirectory && old.Hidden == entry.Hidden &&
                old.Length == entry.Length && old.Updated == entry.Updated)
                entries[i] = old;
        }
    }

    private void UpdateWatchedList(FileEntry[] entries)
    {
        var previous = FileList.ItemsSource as FileEntry[] ?? [];
        if (previous.SequenceEqual(entries)) return;

        var selection = Selection.Select(entry => entry.FullPath).ToHashSet(StringComparer.Ordinal);
        var oldPreview = FileList.SelectedItems.Count == 1 ? FileList.SelectedItem as FileEntry : null;
        var hadFocus = FileList.IsKeyboardFocusWithin;
        var scroll = FindListScrollViewer(FileList);
        var horizontal = scroll?.HorizontalOffset ?? 0;
        var vertical = scroll?.VerticalOffset ?? 0;
        sortingFiles = true;
        try
        {
            FileList.ItemsSource = entries;
            foreach (var entry in entries)
                if (selection.Contains(entry.FullPath)) FileList.SelectedItems.Add(entry);
        }
        finally
        {
            sortingFiles = false;
        }
        if (hadFocus && !FileList.IsKeyboardFocusWithin) FileList.Focus();
        scroll?.ScrollToHorizontalOffset(horizontal);
        scroll?.ScrollToVerticalOffset(vertical);
        CountSelection();

        var newPreview = FileList.SelectedItems.Count == 1 ? FileList.SelectedItem as FileEntry : null;
        if (!ReferenceEquals(oldPreview, newPreview) || newPreview is not null &&
            !previous.Where(item => NiftiFiles.IsMatchDependency(newPreview, item))
                .SequenceEqual(entries.Where(item => NiftiFiles.IsMatchDependency(newPreview, item)))) _ = ShowPreview();
    }

    private static ScrollViewer? FindListScrollViewer(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer scroll) return scroll;
            if (FindListScrollViewer(child) is { } nested) return nested;
        }
        return null;
    }
}
