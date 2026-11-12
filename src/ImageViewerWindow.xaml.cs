using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QuickFileDesk;

public partial class ImageViewerWindow : Window
{
    private static readonly SemaphoreSlim Decoder = new(1);
    private readonly ReviewItem[] images;
    private readonly Dictionary<int, ImageEdits> history = new();
    private readonly List<Color> recentColors = [];
    private int index;
    private CancellationTokenSource? loading;
    private CancellationTokenSource? changingMask;
    private NiftiReview? activeReview;
    private bool saving;
    private string saveStatus = "";

    internal ImageViewerWindow(ReviewItem[] images, int index)
    {
        this.images = images;
        this.index = index;
        InitializeComponent();
        Editor.StateChanged += UpdateEditorControls;
        ColorSwatch.Background = new SolidColorBrush(Editor.StrokeColor);
        UpdatePosition();
        UpdateEditorControls();
    }

    private void UpdatePosition()
    {
        Title = $"{images[index].Name} — PathDeck";
        AutomationProperties.SetName(Editor, images[index].Name);
        ImagePosition.Text = $"{index + 1} / {images.Length}";
        PreviousButton.IsEnabled = index > 0;
        NextButton.IsEnabled = index + 1 < images.Length;
    }

    private async Task ShowImage()
    {
        loading?.Cancel();
        changingMask?.Cancel();
        using var request = new CancellationTokenSource();
        loading = request;
        var item = images[index];
        activeReview = null;
        MaskControls.Visibility = Visibility.Collapsed;
        saveStatus = "";
        Editor.SetImage(null, null, null);
        ImageHint.Text = "正在加载图片…";
        ImageHint.Visibility = Visibility.Visible;
        try
        {
            await Decoder.WaitAsync(request.Token);
            try
            {
                var (bitmap, pixels, review, overlay) = await Task.Run(() =>
                {
                    if (NiftiFiles.IsNifti(item.Name))
                    {
                        var volume = NiftiFiles.Load(item, request.Token);
                        return (volume.Image, volume.Dimensions, (NiftiReview?)volume,
                            volume.Labels is { Classes.Length: > 0 } labels ? labels.MakeBitmap(null, request.Token) : null);
                    }
                    var (image, samples) = BrowserImage.LoadEditor(item.FullPath, request.Token);
                    return (image, samples, (NiftiReview?)null, (BitmapSource?)null);
                }, request.Token);
                if (request.IsCancellationRequested) return;
                if (!history.TryGetValue(index, out var edits)) history[index] = edits = new ImageEdits();
                Editor.SetImage(bitmap, pixels, edits);
                if (review is not null)
                {
                    activeReview = review;
                    Editor.SetReview(review, overlay);
                    ShowMaskOptions(item, review);
                }
                ImageHint.Visibility = Visibility.Collapsed;
            }
            finally
            {
                Decoder.Release();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or FormatException or
                                   NotSupportedException or ArgumentException or COMException)
        {
            if (!request.IsCancellationRequested)
                ImageHint.Text = NiftiFiles.IsNifti(item.Name) ? ex.Message : "无法加载图片，可用右键「打开方式」";
        }
        finally
        {
            if (loading == request) loading = null;
        }
    }

    private async Task Move(int offset)
    {
        var next = index + offset;
        if (next < 0 || next >= images.Length) return;
        index = next;
        UpdatePosition();
        await ShowImage();
    }

    private async void LoadImage(object sender, RoutedEventArgs e)
    {
        ViewerContent.Focus();
        await ShowImage();
    }

    private void CancelLoad(object? sender, EventArgs e)
    {
        loading?.Cancel();
        changingMask?.Cancel();
    }
    private async void PreviousClick(object sender, RoutedEventArgs e) => await Move(-1);
    private async void NextClick(object sender, RoutedEventArgs e) => await Move(1);

    private void UpdateEditorControls()
    {
        ImageStatus.Text = string.IsNullOrEmpty(Editor.PixelReadout)
            ? Editor.SizeReadout : $"{Editor.SizeReadout}    {Editor.PixelReadout}";
        EditStatus.Text = string.IsNullOrEmpty(saveStatus) ? Editor.MarkReadout : saveStatus;
        ZoomStatus.Text = Editor.HasImage ? Editor.Zoom.ToString("P0", CultureInfo.InvariantCulture) : "";
        FitButton.IsEnabled = ActualSizeButton.IsEnabled = Editor.HasImage;
        UndoButton.IsEnabled = Editor.CanUndo;
        RedoButton.IsEnabled = Editor.CanRedo;
        SaveButton.IsEnabled = !saving && Editor.CanExport && Editor.CanUndo;
        SaveButton.ToolTip = Editor.HasImage && !Editor.CanExport ? "图片过大，无法另存全尺寸标注" : "另存为 PNG（Ctrl+S）";
    }

    private void ToolChecked(object sender, RoutedEventArgs e)
    {
        if (Editor is null || sender is not RadioButton { Tag: string name } ||
            !Enum.TryParse<ImageTool>(name, out var tool)) return;
        saveStatus = "";
        Editor.SetTool(tool);
        ViewerContent.Focus();
        UpdateEditorControls();
    }

    private void OpenStrokeMenu(object sender, RoutedEventArgs e)
    {
        StrokeMenu.PlacementTarget = StrokeButton;
        StrokeMenu.IsOpen = true;
    }

    private void SelectStroke(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string width } selected) return;
        Editor.StrokeWidth = int.Parse(width, CultureInfo.InvariantCulture);
        StrokeButton.Content = $"线宽 {width} px  ⌄";
        AutomationProperties.SetName(StrokeButton, $"线宽 {width} 像素");
        foreach (var item in StrokeMenu.Items.OfType<MenuItem>()) item.IsChecked = item == selected;
        StrokeMenu.IsOpen = false;
        ViewerContent.Focus();
    }

    private void ShowMaskOptions(ReviewItem item, NiftiReview review)
    {
        MaskControls.Visibility = Visibility.Visible;
        MaskStatus.Text = item.Mask is null ? review.Status : $"原图 {item.Name}  ·  标注 {item.Mask.Name}  ·  {review.Status}";
        MaskClassMenu.Items.Clear();
        MaskOptions.Visibility = review.Labels is { Classes.Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        if (review.Labels is not { Classes.Length: > 0 } labels) return;
        var all = new MenuItem { Header = "显示全部类别", StaysOpenOnClick = true };
        all.Click += async (_, _) =>
        {
            foreach (var option in MaskClassMenu.Items.OfType<MenuItem>().Where(option => option.IsCheckable))
                option.IsChecked = true;
            await RefreshMask();
        };
        MaskClassMenu.Items.Add(all);
        MaskClassMenu.Items.Add(new Separator());
        foreach (var label in labels.Classes)
        {
            var option = new MenuItem
            {
                Header = $"标签 {label}", Tag = label, IsCheckable = true, IsChecked = true, StaysOpenOnClick = true,
                Icon = new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(3),
                    Background = new SolidColorBrush(NiftiLabels.ClassColor(label)) }
            };
            option.Click += async (_, _) => await RefreshMask();
            MaskClassMenu.Items.Add(option);
        }
    }

    private async Task RefreshMask()
    {
        if (activeReview is not { Labels: { } labels } review) return;
        changingMask?.Cancel();
        using var request = new CancellationTokenSource();
        changingMask = request;
        var hidden = MaskClassMenu.Items.OfType<MenuItem>().Where(item => item.IsCheckable && !item.IsChecked)
            .Select(item => (int)item.Tag).ToHashSet();
        try
        {
            var overlay = await Task.Run(() => labels.MakeBitmap(hidden, request.Token), request.Token);
            if (!request.IsCancellationRequested && ReferenceEquals(activeReview, review)) Editor.SetReview(review, overlay);
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (changingMask == request) changingMask = null;
        }
    }

    private void MaskOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e) =>
        Editor?.SetMaskOpacity(e.NewValue / 100);

    private void OpenMaskClassMenu(object sender, RoutedEventArgs e)
    {
        MaskClassMenu.PlacementTarget = MaskClassButton;
        MaskClassMenu.IsOpen = true;
    }

    private void ChooseColorClick(object sender, RoutedEventArgs e)
    {
        var dialog = new ColorPickerWindow(Editor.StrokeColor, recentColors);
        PathDeckDialogs.Attach(dialog, this);
        if (dialog.ShowDialog() != true) return;
        var color = dialog.SelectedColor;
        recentColors.Remove(color);
        recentColors.Insert(0, color);
        if (recentColors.Count > 8) recentColors.RemoveAt(8);
        Editor.StrokeColor = color;
        ColorSwatch.Background = new SolidColorBrush(color);
        ViewerContent.Focus();
    }

    private void FitClick(object sender, RoutedEventArgs e) => Editor.FitImage();
    private void ActualSizeClick(object sender, RoutedEventArgs e) => Editor.ActualSize();
    private void UndoClick(object sender, RoutedEventArgs e) => Undo();
    private void RedoClick(object sender, RoutedEventArgs e) => Redo();
    private async void SaveClick(object sender, RoutedEventArgs e) => await SaveCopy();

    private void Undo()
    {
        saveStatus = "";
        Editor.Undo();
    }

    private void Redo()
    {
        saveStatus = "";
        Editor.Redo();
    }

    private async Task SaveCopy()
    {
        if (saving || !Editor.CanExport || !Editor.CanUndo || !history.TryGetValue(index, out var edits)) return;
        var dialog = new AnnotationSaveWindow(images[index].FullPath);
        PathDeckDialogs.Attach(dialog, this);
        if (dialog.ShowDialog() != true) return;
        var destination = dialog.OutputPath;

        var marks = edits.Marks.ToArray();
        var savedIndex = index;
        saving = true;
        saveStatus = "正在另存标注…";
        UpdateEditorControls();
        try
        {
            var bitmap = Editor.RenderCopy(marks);
            await Task.Run(() => WritePng(destination, bitmap));
            edits.MarkSaved(marks);
            if (index == savedIndex) saveStatus = "已另存标注 PNG";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
                                   InvalidOperationException or NotSupportedException or COMException)
        {
            saveStatus = "";
            PathDeckDialogs.Notice(this, "无法另存标注", ex.Message);
        }
        finally
        {
            saving = false;
            UpdateEditorControls();
        }
    }

    private static void WritePng(string path, BitmapSource bitmap)
    {
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, $".pathdeck-{Guid.NewGuid():N}.tmp");
        var created = false;
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created = true;
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                encoder.Save(file);
            }
            File.Move(temporary, path);
            created = false;
        }
        finally
        {
            if (created) File.Delete(temporary);
        }
    }

    internal bool ConfirmClose()
    {
        if (saving) return false;
        if (!history.Values.Any(edits => edits.HasUnsavedMarks)) return true;
        return PathDeckDialogs.Confirm(this, "放弃未保存的标注？", "关闭后，未另存的标注会丢失。",
            "放弃并关闭", "继续编辑");
    }

    private void CheckEditsBeforeClose(object? sender, CancelEventArgs e) => e.Cancel = !ConfirmClose();

    private async void HandleKeys(object sender, KeyEventArgs e)
    {
        if (StrokeMenu.IsOpen || MaskClassMenu.IsOpen ||
            (MaskOpacity.IsKeyboardFocusWithin && e.Key is Key.Left or Key.Right)) return;
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (e.Key == Key.Z) { e.Handled = true; Undo(); }
            if (e.Key == Key.Y) { e.Handled = true; Redo(); }
            if (e.Key == Key.S) { e.Handled = true; await SaveCopy(); }
            return;
        }
        if (Keyboard.Modifiers != ModifierKeys.None) return;
        switch (e.Key)
        {
            case Key.Left:
            case Key.Right:
                e.Handled = true;
                await Move(e.Key == Key.Left ? -1 : 1);
                break;
            case Key.Escape:
                e.Handled = true;
                if (!Editor.CancelGesture()) Close();
                break;
        }
    }

    private void OpenWithClick(object sender, RoutedEventArgs e)
    {
        if (WindowsIntegration.OpenWith(this, images[index].FullPath) is string error)
            PathDeckDialogs.Notice(this, "无法打开文件", error);
    }
}
