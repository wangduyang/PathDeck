using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace QuickFileDesk;

public partial class MainWindow
{
    private const double MinPreviewWidth = 150;
    private static readonly SemaphoreSlim PreviewDecoder = new(1);
    private PreviewPixels? previewPixels;
    private NiftiReview? previewNifti;
    private (int X, int Y)? previewPixel;
    private GridLength previewWidth = new(1, GridUnitType.Star);

    private void SetPreviewVisible(bool show)
    {
        if ((PreviewPane.Visibility == Visibility.Visible) == show) return;
        if (show)
        {
            PreviewColumn.MinWidth = MinPreviewWidth;
            PreviewColumn.Width = previewWidth;
        }
        else
        {
            previewWidth = PreviewColumn.Width;
            PreviewColumn.MinWidth = 0;
            PreviewColumn.Width = new GridLength(0);
        }
        PreviewSplitter.Visibility = PreviewPane.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private double previewZoom = 1;
    private Point? previewDragStart;
    private Point previewDragOffset;

    private void PreviewWheel(object sender, MouseWheelEventArgs e)
    {
        if (PreviewImage.Source is not BitmapSource || e.Delta == 0) return;
        e.Handled = true;

        var zoom = Math.Clamp(previewZoom * Math.Pow(1.2, e.Delta / 120d), .25, 8);
        if (Math.Abs(zoom - 1) < .001) zoom = 1;
        if (zoom == previewZoom) return;

        var pointer = e.GetPosition(PreviewViewport);
        var onImage = e.GetPosition(PreviewImage);
        var x = Math.Clamp(onImage.X / Math.Max(1, PreviewImage.ActualWidth), 0, 1);
        var y = Math.Clamp(onImage.Y / Math.Max(1, PreviewImage.ActualHeight), 0, 1);
        previewZoom = zoom;
        SizePreviewImage();

        PreviewViewport.ScrollToHorizontalOffset((PreviewCanvas.Width - PreviewImage.Width) / 2 + x * PreviewImage.Width - pointer.X);
        PreviewViewport.ScrollToVerticalOffset((PreviewCanvas.Height - PreviewImage.Height) / 2 + y * PreviewImage.Height - pointer.Y);
    }

    private void SizePreviewImage()
    {
        if (PreviewImage?.Source is not BitmapSource bitmap || PreviewViewport.ActualWidth <= 0 || PreviewViewport.ActualHeight <= 0)
            return;
        var width = PreviewViewport.ActualWidth;
        var height = PreviewViewport.ActualHeight;
        var fit = Math.Min(Math.Max(1, width - 10) / bitmap.PixelWidth, Math.Max(1, height - 10) / bitmap.PixelHeight);
        PreviewImage.Width = bitmap.PixelWidth * fit * previewZoom;
        PreviewImage.Height = bitmap.PixelHeight * fit * previewZoom;
        PreviewCanvas.Width = Math.Max(width, PreviewImage.Width + 10);
        PreviewCanvas.Height = Math.Max(height, PreviewImage.Height + 10);
        PreviewViewport.Cursor = PreviewCanvas.Width > width + 1 || PreviewCanvas.Height > height + 1 ? Cursors.Hand : null;
        PreviewZoomButton.Content = previewZoom == 1 ? "适应" : $"{previewZoom:P0}";
        PreviewZoomButton.Visibility = Visibility.Visible;
    }

    private void ResetPreviewZoom()
    {
        if (PreviewViewport.IsMouseCaptured) PreviewViewport.ReleaseMouseCapture();
        previewDragStart = null;
        previewZoom = 1;
        PreviewViewport.ScrollToHorizontalOffset(0);
        PreviewViewport.ScrollToVerticalOffset(0);
        if (PreviewImage.Source is BitmapSource)
        {
            SizePreviewImage();
            return;
        }
        PreviewImage.Width = double.NaN;
        PreviewImage.Height = double.NaN;
        PreviewCanvas.Width = double.NaN;
        PreviewCanvas.Height = double.NaN;
        PreviewViewport.Cursor = null;
        PreviewZoomButton.Visibility = Visibility.Collapsed;
    }

    private void PreviewViewportResized(object sender, SizeChangedEventArgs e) => SizePreviewImage();

    private void ResetPreviewClick(object sender, RoutedEventArgs e) => ResetPreviewZoom();

    private void StartPreviewDrag(object sender, MouseButtonEventArgs e)
    {
        if (PreviewImage.Source is not BitmapSource || PreviewViewport.Cursor != Cursors.Hand || !PreviewViewport.CaptureMouse())
            return;
        previewDragStart = e.GetPosition(PreviewViewport);
        previewDragOffset = new(PreviewViewport.HorizontalOffset, PreviewViewport.VerticalOffset);
        e.Handled = true;
    }

    private void MovePreviewDrag(object sender, MouseEventArgs e)
    {
        if (previewDragStart is Point start && e.LeftButton == MouseButtonState.Pressed)
        {
            var delta = start - e.GetPosition(PreviewViewport);
            PreviewViewport.ScrollToHorizontalOffset(previewDragOffset.X + delta.X);
            PreviewViewport.ScrollToVerticalOffset(previewDragOffset.Y + delta.Y);
            e.Handled = true;
        }
        UpdatePreviewPixel();
    }

    private void PreviewGeometryChanged(object sender, RoutedEventArgs e) => UpdatePreviewPixel();

    private void LeavePreview(object sender, MouseEventArgs e) => ClearPreviewPixel();

    private void UpdatePreviewPixel()
    {
        if (previewPixels is null || !PreviewViewport.IsMouseOver ||
            !new Rect(PreviewViewport.RenderSize).Contains(Mouse.GetPosition(PreviewViewport)))
        {
            ClearPreviewPixel();
            return;
        }

        ShowPreviewPixel(Mouse.GetPosition(PreviewImage));
    }

    private void ShowPreviewPixel(Point position)
    {
        var width = PreviewImage.ActualWidth;
        var height = PreviewImage.ActualHeight;
        if (previewPixels is not { } pixels || position.X < 0 || position.Y < 0 ||
            position.X >= width || position.Y >= height)
        {
            ClearPreviewPixel();
            return;
        }

        var x = Math.Min((int)(position.X / width * pixels.Width), pixels.Width - 1);
        var y = Math.Min((int)(position.Y / height * pixels.Height), pixels.Height - 1);
        if (previewPixel == (x, y)) return;
        previewPixel = (x, y);
        PreviewCoordinates.Text = $"坐标 ({x}, {y})";
        PreviewPixelValue.Text = previewNifti?.PixelAt(x, y) ?? pixels.ValueAt(x, y);
        PreviewPixelStatus.Visibility = Visibility.Visible;
    }

    private void ClearPreviewPixel()
    {
        if (previewPixel is null) return;
        previewPixel = null;
        PreviewPixelStatus.Visibility = Visibility.Hidden;
    }

    private void StopPreviewDrag(object sender, MouseButtonEventArgs e)
    {
        if (!PreviewViewport.IsMouseCaptured) return;
        PreviewViewport.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void PreviewDragCaptureLost(object sender, MouseEventArgs e) => previewDragStart = null;
}

