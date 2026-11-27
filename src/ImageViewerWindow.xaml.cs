using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
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
    private readonly Dictionary<int, ImageSource> history = new();
    private readonly List<Color> recentColors = [];
    private int index;
    private CancellationTokenSource? loading;
    private CancellationTokenSource? changingMask;
    private NiftiReview? activeReview;
    private ImageSource? activeSource;
    private int activeIndex;
    private bool saving;
    private string saveStatus = "";

    private sealed record ImageSource(BitmapSource Bitmap, PreviewPixels Pixels, NiftiReview? Review,
        BitmapSource? Overlay, string Version, ImageEdits Edits)
    {
        public bool HasWork => Edits.Marks.Count > 0 || Edits.Undone.Count > 0;
        public bool SameSource(ImageSource other) => Version == other.Version &&
            Pixels.Width == other.Pixels.Width && Pixels.Height == other.Pixels.Height;
    }

    private static FileStream OpenSource(string path)
    {
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (NiftiFiles.IsNifti(path) || file.Length <= BrowserImage.MaxFileSize) return file;
        file.Dispose();
        throw new NotSupportedException("图片文件超过 64 MB，无法在标注窗口中加载。");
    }

    private static FileStream? OpenMask(ReviewItem item)
    {
        if (item.Mask is null) return null;
        try { return OpenSource(item.Mask.FullPath); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return null; }
    }

    private static string SourceVersion(FileStream image, FileStream? mask, CancellationToken token)
    {
        string Hash(FileStream file)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[65536];
            int count;
            while ((count = file.Read(buffer)) > 0)
            {
                token.ThrowIfCancellationRequested();
                hash.AppendData(buffer, 0, count);
            }
            token.ThrowIfCancellationRequested();
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        return Hash(image) + ":" + (mask is null ? "" : Hash(mask));
    }

    private static ImageSource ReadSource(ReviewItem item, CancellationToken token)
    {
        // These read locks keep the fingerprint and the decoder on the same source bytes.
        using var imageFile = OpenSource(item.FullPath);
        using var maskFile = OpenMask(item);
        if (NiftiFiles.IsNifti(item.Name))
        {
            var volume = NiftiFiles.Load(maskFile is null ? item with { Mask = null } : item, token);
            return new(volume.Image, volume.Dimensions, volume,
                volume.Labels is { Classes.Length: > 0 } labels ? labels.MakeBitmap(null, token) : null,
                SourceVersion(imageFile, maskFile, token), new ImageEdits());
        }
        var (bitmap, pixels) = BrowserImage.LoadEditor(item.FullPath, token);
        return new(bitmap, pixels, null, null, SourceVersion(imageFile, maskFile, token), new ImageEdits());
    }

    private static bool SourceChanged(ReviewItem item, ImageSource source, CancellationToken token)
    {
        try
        {
            using var imageFile = OpenSource(item.FullPath);
            using var maskFile = OpenMask(item);
            return source.Version != SourceVersion(imageFile, maskFile, token);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException) { return true; }
    }

    private bool DiscardChangedSource(ReviewItem item) => PathDeckDialogs.Confirm(this, "源文件已改变",
        $"「{item.Name}」已被其他程序修改。保留旧图可继续编辑并另存原有标注；重新加载会放弃这张旧图上的标注。",
        "重新加载并放弃旧标注", "保留旧图和标注");

    private void DisplaySource(int requestedIndex, ImageSource source)
    {
        activeSource = source;
        activeIndex = requestedIndex;
        Editor.SetImage(source.Bitmap, source.Pixels, source.Edits);
        if (source.Review is { } review)
        {
            activeReview = review;
            Editor.SetReview(review, source.Overlay);
            ShowMaskOptions(images[requestedIndex], review);
        }
        var linkedView = ComparisonPeers().Select(viewer => viewer.Editor.Viewport).FirstOrDefault(view => view.HasValue);
        if (linkedView is { } view) Editor.ApplyViewport(view);
        ImageHint.Visibility = Visibility.Collapsed;
        UpdateEditorControls();
    }

    internal ImageViewerWindow(ReviewItem[] images, int index)
    {
        this.images = images;
        this.index = index;
        InitializeComponent();
        Editor.StateChanged += UpdateEditorControls;
        Editor.ViewNavigated += SyncView;
        Editor.PointerMoved += SyncPointer;
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

    private IEnumerable<ImageViewerWindow> OtherViewers() => Application.Current.Windows
        .OfType<ImageViewerWindow>().Where(viewer => !ReferenceEquals(viewer, this));

    private ImageViewerWindow[] ComparisonPeers()
    {
        if (Editor.ImageDimensions is not { } dimensions) return [];
        var viewers = OtherViewers().ToArray();
        return viewers.All(viewer => viewer.Editor.ImageDimensions == dimensions) ? viewers : [];
    }

    private void SyncView(ImageViewport view)
    {
        foreach (var viewer in ComparisonPeers())
            viewer.Editor.ApplyViewport(view);
    }

    private void SyncPointer(Point? position)
    {
        Editor.SetLinkedPointer(null);
        foreach (var viewer in ComparisonPeers())
            viewer.Editor.SetLinkedPointer(position);
    }

    private void ClearComparisonPointers()
    {
        Editor.SetLinkedPointer(null);
        foreach (var viewer in OtherViewers())
            viewer.Editor.SetLinkedPointer(null);
    }

    private async Task ShowImage()
    {
        loading?.Cancel();
        changingMask?.Cancel();
        using var request = new CancellationTokenSource();
        loading = request;
        var requestedIndex = index;
        var item = images[requestedIndex];
        var departed = activeSource;
        var departedIndex = activeIndex;
        foreach (var unused in history.Where(pair => pair.Key != requestedIndex && !pair.Value.HasWork).Select(pair => pair.Key).ToArray())
            history.Remove(unused);
        activeSource = null;
        activeReview = null;
        MaskControls.Visibility = Visibility.Collapsed;
        saveStatus = "";
        ClearComparisonPointers();
        Editor.SetImage(null, null, null);
        ImageHint.Text = "正在加载图片…";
        ImageHint.Visibility = Visibility.Visible;
        try
        {
            await Decoder.WaitAsync(request.Token);
            try
            {
                bool IsCurrent() => !request.IsCancellationRequested && ReferenceEquals(loading, request) && index == requestedIndex;
                if (departed is { HasWork: true } && departedIndex != requestedIndex &&
                    await Task.Run(() => SourceChanged(images[departedIndex], departed, request.Token), request.Token))
                {
                    if (!IsCurrent()) return;
                    var discard = DiscardChangedSource(images[departedIndex]);
                    if (!IsCurrent()) return;
                    if (discard) history.Remove(departedIndex);
                }
                var source = await Task.Run(() => ReadSource(item, request.Token), request.Token);
                if (!IsCurrent()) return;
                if (history.TryGetValue(requestedIndex, out var previous))
                {
                    if (previous.SameSource(source)) source = source with { Edits = previous.Edits };
                    else if (previous.HasWork)
                    {
                        var discard = DiscardChangedSource(item);
                        if (!IsCurrent()) return;
                        if (!discard)
                        {
                            source = previous;
                            saveStatus = "源文件已改变 · 正在编辑保留的旧图";
                        }
                    }
                }
                history[requestedIndex] = source;
                DisplaySource(requestedIndex, source);
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
            if (!request.IsCancellationRequested && ReferenceEquals(loading, request) && index == requestedIndex)
            {
                if (history.TryGetValue(requestedIndex, out var previous) && previous.HasWork)
                {
                    saveStatus = "源文件无法读取 · 正在编辑保留的旧图";
                    DisplaySource(requestedIndex, previous);
                }
                else ImageHint.Text = NiftiFiles.IsNifti(item.Name) ? ex.Message : "无法加载图片，可用右键「打开方式」";
            }
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
        ClearComparisonPointers();
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
        if (saving || !Editor.CanExport || !Editor.CanUndo || activeSource is not { } source) return;
        var savedIndex = index;
        var dialog = new AnnotationSaveWindow(images[index].FullPath);
        PathDeckDialogs.Attach(dialog, this);
        if (dialog.ShowDialog() != true) return;
        if (!ReferenceEquals(activeSource, source) || index != savedIndex) return;
        var destination = dialog.OutputPath;

        var edits = source.Edits;
        var marks = edits.Marks.ToArray();
        saving = true;
        saveStatus = "正在另存标注…";
        UpdateEditorControls();
        try
        {
            var bitmap = Editor.RenderCopy(marks);
            await Task.Run(() => WritePng(destination, bitmap));
            edits.MarkSaved(marks);
            if (index == savedIndex && activeSource?.Edits == edits) saveStatus = "已另存标注 PNG";
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
        if (!history.Values.Any(source => source.Edits.HasUnsavedMarks)) return true;
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
