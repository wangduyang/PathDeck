using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QuickFileDesk;

internal readonly record struct ImageViewport(bool IsFit, double Zoom, Point RelativeCenter);

public sealed class ImageEditorCanvas : FrameworkElement
{
    private static readonly Lazy<(BitmapSource Bitmap, Point Hotspot)?> HandCursor = new(WindowsIntegration.ReadHandCursor);

    private BitmapSource? bitmap;
    private BitmapSource? mask;
    private NiftiReview? review;
    private double maskOpacity = .5;
    private PreviewPixels? pixels;
    private ImageEdits? edits;
    private ImageMark? draft;
    private bool fit = true;
    private double scale = 1;
    private Point origin;
    private Point dragStart;
    private Point dragOrigin;
    private MouseButton? panButton;
    private Point? hoveredPixel;
    private Point? linkedPointer;

    private ImageViewport? navigation;

    internal event Action? StateChanged;
    internal event Action<ImageViewport>? ViewNavigated;
    internal event Action<Point?>? PointerMoved;
    internal ImageTool Tool { get; private set; }
    internal Color StrokeColor { get; set; } = Color.FromRgb(255, 100, 53);
    internal double StrokeWidth { get; set; } = 3;
    internal string PixelReadout { get; private set; } = "";
    internal string MarkReadout => draft?.Readout ?? edits?.Marks.LastOrDefault()?.Readout ?? "";
    internal string SizeReadout => pixels is null ? "" : $"{pixels.Width} × {pixels.Height} px";
    internal double Zoom => scale * VisualTreeHelper.GetDpi(this).DpiScaleX;
    internal bool HasImage => bitmap is not null;
    internal (int Width, int Height)? ImageDimensions =>
        HasImage && pixels is { } samples ? (samples.Width, samples.Height) : null;
    internal ImageViewport? Viewport => navigation;
    internal bool CanUndo => edits?.Marks.Count > 0;
    internal bool CanRedo => edits?.Undone.Count > 0;
    internal bool CanExport => bitmap is not null && pixels is not null &&
        bitmap.PixelWidth == pixels.Width && bitmap.PixelHeight == pixels.Height;

    public ImageEditorCanvas()
    {
        ClipToBounds = true;
        UpdateCursor();
    }

    internal void SetImage(BitmapSource? source, PreviewPixels? samples, ImageEdits? imageEdits)
    {
        CancelGesture();
        bitmap = source;
        pixels = samples;
        edits = imageEdits;
        mask = null;
        review = null;
        ClearPixel();
        ResetView();
    }

    internal void SetReview(NiftiReview? source, BitmapSource? overlay)
    {
        review = source;
        mask = overlay;
        ClearPixel();
        Changed();
    }

    internal void SetLinkedPointer(Point? imagePoint)
    {
        if (linkedPointer == imagePoint) return;
        linkedPointer = imagePoint;
        InvalidateVisual();
    }

    internal void SetMaskOpacity(double value)
    {
        maskOpacity = value;
        InvalidateVisual();
    }

    internal void SetTool(ImageTool tool)
    {
        CancelGesture();
        Tool = tool;
        UpdateCursor();
    }

    internal void FitImage()
    {
        ResetView();
        PublishViewport();
    }

    private void ResetView()
    {
        navigation = null;
        fit = true;
        scale = FitScale();
        ViewChanged();
    }

    internal void ApplyViewport(ImageViewport view)
    {
        if (pixels is not { } samples || !HasImage) return;
        navigation = view;
        fit = view.IsFit;
        scale = fit ? FitScale() : view.Zoom / VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (!fit)
        {
            origin = new Point(
                ActualWidth / 2 - view.RelativeCenter.X * samples.Width * scale,
                ActualHeight / 2 - view.RelativeCenter.Y * samples.Height * scale);
        }
        ClearPixel();
        ViewChanged();
    }

    private void PublishViewport()
    {
        if (pixels is not { } samples || !HasImage) return;
        var center = ImagePoint(new Point(ActualWidth / 2, ActualHeight / 2));
        var view = new ImageViewport(fit, Zoom, new Point(center.X / samples.Width, center.Y / samples.Height));
        navigation = view;
        ViewNavigated?.Invoke(view);
    }

    internal void ActualSize()
    {
        if (pixels is null) return;
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        ZoomAt(new Point(ActualWidth / 2, ActualHeight / 2), 1 / dpi);
    }

    private double FitScale() => pixels is null ? 1 :
        Math.Min(Math.Max(1, ActualWidth) / pixels.Width, Math.Max(1, ActualHeight) / pixels.Height);

    private Point ImagePoint(Point viewport) => new((viewport.X - origin.X) / scale, (viewport.Y - origin.Y) / scale);

    private void ZoomAt(Point anchor, double newScale)
    {
        var point = ImagePoint(anchor);
        fit = false;
        scale = newScale;
        origin = new Point(anchor.X - point.X * scale, anchor.Y - point.Y * scale);
        ViewChanged();
        PublishViewport();
    }

    private static double Constrain(double offset, double viewport, double drawn) =>
        drawn <= viewport ? (viewport - drawn) / 2 : Math.Clamp(offset, viewport - drawn, 0);

    private void ViewChanged()
    {
        if (pixels is not null)
        {
            if (fit) origin = new Point((ActualWidth - pixels.Width * scale) / 2, (ActualHeight - pixels.Height * scale) / 2);
            else origin = new Point(Constrain(origin.X, ActualWidth, pixels.Width * scale),
                Constrain(origin.Y, ActualHeight, pixels.Height * scale));
        }
        RenderOptions.SetBitmapScalingMode(this, Zoom >= 1.5 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
        Changed();
    }

    private void Changed()
    {
        InvalidateVisual();
        StateChanged?.Invoke();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (navigation is { } view) ApplyViewport(view);
        else ResetView();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        if (navigation is { } view) ApplyViewport(view);
        else ResetView();
    }

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        drawing.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        if (bitmap is null || pixels is null) return;
        drawing.PushTransform(new MatrixTransform(scale, 0, 0, scale, origin.X, origin.Y));
        var dpi = VisualTreeHelper.GetDpi(this);
        DrawImage(drawing, edits?.Marks, draft, 1.5 / scale, 13 / scale, dpi.PixelsPerDip);
        drawing.Pop();

        if (linkedPointer is not { } point) return;
        var position = new Point(origin.X + point.X * scale, origin.Y + point.Y * scale);
        if (!new Rect(RenderSize).Contains(position) || HandCursor.Value is not { } hand) return;
        drawing.DrawImage(hand.Bitmap, new Rect(
            position.X - hand.Hotspot.X / dpi.DpiScaleX, position.Y - hand.Hotspot.Y / dpi.DpiScaleY,
            hand.Bitmap.PixelWidth / dpi.DpiScaleX, hand.Bitmap.PixelHeight / dpi.DpiScaleY));
    }

    private void DrawImage(DrawingContext drawing, IEnumerable<ImageMark>? marks, ImageMark? active,
        double minStroke, double labelSize, double pixelsPerDip)
    {
        if (bitmap is null || pixels is null) return;
        var bounds = new Rect(0, 0, pixels.Width, pixels.Height);
        drawing.PushClip(new RectangleGeometry(bounds));
        drawing.DrawImage(bitmap, bounds);
        if (mask is not null && maskOpacity > 0)
        {
            drawing.PushOpacity(maskOpacity);
            drawing.DrawImage(mask, bounds);
            drawing.Pop();
        }
        if (marks is not null)
            foreach (var mark in marks)
                mark.Draw(drawing, bounds, minStroke, labelSize, pixelsPerDip);
        active?.Draw(drawing, bounds, minStroke, labelSize, pixelsPerDip);
        drawing.Pop();
    }

    internal BitmapSource RenderCopy(ImageMark[] marks)
    {
        if (!CanExport || pixels is not { } samples) throw new InvalidOperationException("此图片过大，无法导出全尺寸标注。");
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
            DrawImage(drawing, marks, null, 0, Math.Clamp(Math.Min(samples.Width, samples.Height) / 45d, 12, 40), 1);
        var result = new RenderTargetBitmap(samples.Width, samples.Height, 96, 96, PixelFormats.Pbgra32);
        result.Render(visual);
        result.Freeze();
        return result;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (bitmap is null || IsMouseCaptured || e.Delta == 0) return;
        var anchor = e.GetPosition(this);
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var limit = FitScale();
        var value = Math.Clamp(scale * Math.Pow(1.2, e.Delta / 120d),
            Math.Min(limit / 2, 1 / (32 * dpi)), Math.Max(limit, 32 / dpi));
        if (value != scale)
        {
            ZoomAt(anchor, value);
            ReadPixel(anchor);
        }
        e.Handled = true;
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (pixels is null || edits is null || IsMouseCaptured || e.ChangedButton == MouseButton.Right) return;
        var viewport = e.GetPosition(this);
        if (e.ChangedButton == MouseButton.Middle || (e.ChangedButton == MouseButton.Left && Tool == ImageTool.Pan))
        {
            if (!CaptureMouse()) return;
            panButton = e.ChangedButton;
            dragStart = viewport;
            dragOrigin = origin;
            UpdateCursor();
        }
        else if (e.ChangedButton == MouseButton.Left)
        {
            var point = ImagePoint(viewport);
            if (!OnImage(point) || !CaptureMouse()) return;
            draft = new ImageMark(Tool, StrokeColor, StrokeWidth, MarkPoint(point));
            Changed();
        }
        else return;
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var viewport = e.GetPosition(this);
        if (panButton is not null && !fit)
        {
            origin = dragOrigin + (viewport - dragStart);
            ViewChanged();
            PublishViewport();
        }
        else if (draft is not null)
        {
            draft.Extend(MarkPoint(ImagePoint(viewport)));
            Changed();
        }
        ReadPixel(viewport);
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (panButton == e.ChangedButton)
        {
            CancelGesture();
            e.Handled = true;
        }
        else if (e.ChangedButton == MouseButton.Left && draft is { } mark && edits is { } state)
        {
            mark.Extend(MarkPoint(ImagePoint(e.GetPosition(this))));
            if (mark.CanCommit)
            {
                state.Marks.Add(mark);
                state.Undone.Clear();
            }
            CancelGesture();
            e.Handled = true;
        }
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        CancelGesture();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        PointerMoved?.Invoke(null);
        ClearPixel();
    }

    internal bool CancelGesture()
    {
        if (panButton is null && draft is null) return false;
        panButton = null;
        draft = null;
        if (IsMouseCaptured) ReleaseMouseCapture();
        UpdateCursor();
        Changed();
        return true;
    }

    private void UpdateCursor() => Cursor = panButton is not null || Tool == ImageTool.Pan ? Cursors.Hand : Cursors.Cross;

    internal void Undo()
    {
        if (edits is not { Marks.Count: > 0 } state) return;
        CancelGesture();
        state.Undone.Push(state.Marks[^1]);
        state.Marks.RemoveAt(state.Marks.Count - 1);
        Changed();
    }

    internal void Redo()
    {
        if (edits is not { Undone.Count: > 0 } state) return;
        CancelGesture();
        state.Marks.Add(state.Undone.Pop());
        Changed();
    }

    private bool OnImage(Point point) => pixels is { } samples && point.X >= 0 && point.Y >= 0 &&
        point.X < samples.Width && point.Y < samples.Height;

    private Point MarkPoint(Point point)
    {
        var x = Math.Clamp(point.X, 0, pixels!.Width - .001);
        var y = Math.Clamp(point.Y, 0, pixels.Height - .001);
        if (Tool is ImageTool.Measure or ImageTool.Rectangle or ImageTool.Ellipse)
            return new Point(Math.Floor(x) + .5, Math.Floor(y) + .5);
        return new Point(x, y);
    }

    private void ReadPixel(Point viewport)
    {
        var point = ImagePoint(viewport);
        if (pixels is not { } samples || !new Rect(RenderSize).Contains(viewport) || !OnImage(point))
        {
            PointerMoved?.Invoke(null);
            ClearPixel();
            return;
        }
        PointerMoved?.Invoke(point);
        var x = (int)point.X;
        var y = (int)point.Y;
        var current = new Point(x, y);
        if (hoveredPixel == current) return;
        hoveredPixel = current;
        PixelReadout = $"({x}, {y}) · {review?.PixelAt(x, y) ?? samples.ValueAt(x, y)}";
        StateChanged?.Invoke();
    }

    private void ClearPixel()
    {
        if (hoveredPixel is null) return;
        hoveredPixel = null;
        PixelReadout = "";
        StateChanged?.Invoke();
    }
}
