using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QuickFileDesk;

internal static class BrowserImage
{
    public const long MaxFileSize = 64 * 1024 * 1024;

    public static BitmapSource Load(string path, int longestEdge)
    {
        using var file = Open(path);
        var frame = BitmapDecoder.Create(file, BitmapCreateOptions.DelayCreation, BitmapCacheOption.OnDemand).Frames[0];
        return LoadScaled(file, frame.PixelWidth, frame.PixelHeight, longestEdge);
    }

    public static (BitmapSource Image, PreviewPixels Pixels) LoadPreview(string path, CancellationToken token) =>
        LoadImageAndPixels(path, token, fullSize: false);

    public static (BitmapSource Image, PreviewPixels Pixels) LoadEditor(string path, CancellationToken token) =>
        LoadImageAndPixels(path, token, fullSize: true);

    private static (BitmapSource Image, PreviewPixels Pixels) LoadImageAndPixels(string path, CancellationToken token, bool fullSize)
    {
        using var file = Open(path);
        var frame = BitmapDecoder.Create(file, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
            BitmapCacheOption.OnDemand).Frames[0];
        var width = frame.PixelWidth;
        var height = frame.PixelHeight;
        token.ThrowIfCancellationRequested();

        PreviewPixels pixels;
        try { pixels = PreviewPixels.Read(frame); }
        catch (Exception ex) when (ex is IOException or FormatException or NotSupportedException or ArgumentException or COMException)
        {
            pixels = new PreviewPixels(width, height);
        }
        token.ThrowIfCancellationRequested();

        var edge = Math.Max(width, height);
        var displayEdge = fullSize && edge <= 16384 && (long)width * height <= 32_000_000 ? edge : 4096;
        return (LoadScaled(file, width, height, displayEdge), pixels);
    }

    private static FileStream Open(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    private static BitmapSource LoadScaled(FileStream file, int width, int height, int longestEdge)
    {
        file.Position = 0;
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = file;
        if (width >= height) bitmap.DecodePixelWidth = Math.Min(width, longestEdge);
        else bitmap.DecodePixelHeight = Math.Min(height, longestEdge);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }
}

internal sealed class PreviewPixels(int width, int height, PixelFormat format = default, int stride = 0, byte[]? samples = null)
{
    private const int MaxSampleBytes = 64 * 1024 * 1024;

    public int Width { get; } = width;
    public int Height { get; } = height;

    public static PreviewPixels Read(BitmapSource source)
    {
        var format = source.Format;
        var gray = format == PixelFormats.BlackWhite || format == PixelFormats.Gray2 || format == PixelFormats.Gray4 ||
            format == PixelFormats.Gray8 || format == PixelFormats.Gray16 || format == PixelFormats.Gray32Float;
        var wideColor = format == PixelFormats.Rgb48 || format == PixelFormats.Rgba64;
        var bits = gray || wideColor ? format.BitsPerPixel : 32;
        var rowBytes = ((long)source.PixelWidth * bits + 7) / 8;
        if (rowBytes > MaxSampleBytes / source.PixelHeight)
            return new(source.PixelWidth, source.PixelHeight);

        if (!gray && !wideColor && format != PixelFormats.Bgra32)
            source = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var stride = (int)rowBytes;
        var samples = new byte[stride * source.PixelHeight];
        source.CopyPixels(samples, stride, 0);
        return new(source.PixelWidth, source.PixelHeight, source.Format, stride, samples);
    }

    public string ValueAt(int x, int y)
    {
        if (samples is null) return "像素值不可用";
        var bits = format.BitsPerPixel;
        var index = y * stride + x * bits / 8;
        if (bits <= 8)
        {
            var shift = 8 - bits - x * bits % 8;
            return $"灰度 {(samples[index] >> shift) & ((1 << bits) - 1)}";
        }
        if (format == PixelFormats.Gray16) return $"灰度 {BitConverter.ToUInt16(samples, index)}";
        if (format == PixelFormats.Gray32Float) return $"灰度 {BitConverter.ToSingle(samples, index):G9}";
        if (format == PixelFormats.Rgb48 || format == PixelFormats.Rgba64)
            return $"RGB {BitConverter.ToUInt16(samples, index)},{BitConverter.ToUInt16(samples, index + 2)},{BitConverter.ToUInt16(samples, index + 4)}";
        return $"RGB {samples[index + 2]},{samples[index + 1]},{samples[index]}";
    }
}

public sealed class FileThumbnail : Image
{
    private static readonly SemaphoreSlim ThumbnailSlots = new(2);
    private CancellationTokenSource? loading;

    public static readonly DependencyProperty ActiveProperty = DependencyProperty.Register(
        nameof(Active), typeof(bool), typeof(FileThumbnail),
        new PropertyMetadata(false, (target, _) => ((FileThumbnail)target).Reload()));

    public bool Active
    {
        get => (bool)GetValue(ActiveProperty);
        set => SetValue(ActiveProperty, value);
    }

    public FileThumbnail()
    {
        Loaded += (_, _) => Reload();
        Unloaded += (_, _) =>
        {
            loading?.Cancel();
            Source = null;
        };
        DataContextChanged += (_, _) => Reload();
    }

    private async void Reload()
    {
        loading?.Cancel();
        Source = null;
        if (!IsLoaded || DataContext is not FileEntry entry) return;
        if (!entry.IsShortcut && (!Active || !entry.IsImage || entry.Length is <= 0 or > BrowserImage.MaxFileSize))
            return;

        using var request = new CancellationTokenSource();
        loading = request;
        try
        {
            var image = entry.IsShortcut
                ? await WindowsIntegration.ReadShortcutIconAsync(entry.FullPath, request.Token)
                : await LoadThumbnail(entry.FullPath, request.Token);
            if (!request.IsCancellationRequested) Source = image;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or NotSupportedException or ArgumentException or COMException)
        {
            // Keep the regular file icon when no image is available.
        }
        finally
        {
            if (loading == request) loading = null;
        }
    }

    private static async Task<BitmapSource> LoadThumbnail(string path, CancellationToken token)
    {
        await ThumbnailSlots.WaitAsync(token).ConfigureAwait(false);
        try { return await Task.Run(() => BrowserImage.Load(path, 144), token).ConfigureAwait(false); }
        finally { ThumbnailSlots.Release(); }
    }
}

