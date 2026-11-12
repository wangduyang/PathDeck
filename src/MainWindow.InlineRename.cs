using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace QuickFileDesk;

public partial class MainWindow
{
    private (FileEntry Entry, int Timestamp)? lastNameClick;
    private (TextBlock Label, FileEntry Entry, Point Point, int Timestamp, bool Rename)? pendingNameClick;
    private (TextBlock Label, TextBox Editor, FileEntry Entry, BrowserTab Tab, string Path)? inlineRename;

    private void FileNamePressed(object sender, MouseButtonEventArgs e)
    {
        var first = lastNameClick;
        lastNameClick = null;
        pendingNameClick = null;
        if (working || e.ClickCount != 1 || Keyboard.Modifiers != ModifierKeys.None ||
            sender is not TextBlock { DataContext: FileEntry entry } label)
            return;

        var rename = false;
        if (first is { } previous && previous.Entry == entry && FileList.SelectedItems.Count == 1 && FileList.SelectedItem == entry)
        {
            var elapsed = unchecked(e.Timestamp - previous.Timestamp);
            var minimum = (int)GetDoubleClickTime();
            rename = elapsed >= minimum && elapsed <= minimum + 1300;
        }
        pendingNameClick = (label, entry, e.GetPosition(FileList), e.Timestamp, rename);
    }

    private void FileNameReleased(object sender, MouseButtonEventArgs e)
    {
        var pending = pendingNameClick;
        pendingNameClick = null;
        if (pending is not { } click || !ReferenceEquals(click.Label, sender) || click.Label.DataContext != click.Entry)
            return;
        var distance = e.GetPosition(FileList) - click.Point;
        if (Math.Abs(distance.X) >= SystemParameters.MinimumHorizontalDragDistance ||
            Math.Abs(distance.Y) >= SystemParameters.MinimumVerticalDragDistance) return;

        if (click.Rename && FileList.SelectedItems.Count == 1 && FileList.SelectedItem == click.Entry &&
            BeginInlineRename(click.Label, click.Entry))
        {
            e.Handled = true;
            return;
        }
        lastNameClick = (click.Entry, click.Timestamp);
    }

    private bool BeginInlineRename(TextBlock label, FileEntry entry)
    {
        if (working || inlineRename is not null || Current is not { Loaded: true } tab ||
            label.Parent is not Grid cell || cell.Children.OfType<TextBox>().FirstOrDefault() is not { } editor)
            return false;

        inlineRename = (label, editor, entry, tab, tab.Path);
        editor.Text = entry.Name;
        label.Visibility = Visibility.Collapsed;
        editor.Visibility = Visibility.Visible;
        editor.Focus();
        var stemLength = entry.IsDirectory ? entry.Name.Length : Path.GetFileNameWithoutExtension(entry.Name).Length;
        editor.Select(0, stemLength == 0 ? entry.Name.Length : stemLength);
        return true;
    }

    private async void InlineNameKey(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Escape)) return;
        e.Handled = true;
        await EndInlineRename(e.Key == Key.Enter);
        FileList.Focus();
    }

    private async void InlineNameLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (inlineRename is { } active && ReferenceEquals(active.Editor, sender)) await EndInlineRename(save: true);
    }

    private void InlineNameUnloaded(object sender, RoutedEventArgs e)
    {
        if (inlineRename is { } active && ReferenceEquals(active.Editor, sender)) _ = EndInlineRename(save: false);
    }

    private async Task EndInlineRename(bool save)
    {
        if (inlineRename is not { } active) return;
        inlineRename = null;
        var name = active.Editor.Text;
        active.Editor.Visibility = Visibility.Collapsed;
        active.Label.Visibility = Visibility.Visible;
        if (save && !working && Current == active.Tab && FileOperations.SamePath(active.Tab.Path, active.Path) &&
            active.Editor.DataContext == active.Entry && name != active.Entry.Name)
            await RenameEntry(active.Tab, active.Entry, name);
    }

    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();
}

