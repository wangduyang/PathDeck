using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TaskGlass;

internal sealed record BackdropFrame(BitmapSource Image, System.Windows.Rect Bounds);

// Small, in-memory backdrop samples taken when cards appear or move. Nothing is saved.
internal static class GlassSnapshot
{
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(nint window, uint affinity);
    [DllImport("user32.dll")] private static extern bool GetWindowDisplayAffinity(nint window, out uint affinity);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint window, out Native.Rect bounds);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint window, ref ScreenPoint point);
    [StructLayout(LayoutKind.Sequential)] private struct ScreenPoint { internal int X, Y; }

    internal static BitmapSource? CaptureClient(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        return CaptureAtHandle(handle, client: true);
    }

    internal static Task<BitmapSource?> CaptureClientAsync(Window window) => QueueCapture(window, client: true);
    internal static Task<BitmapSource?> CaptureAsync(Window window) => QueueCapture(window, client: false);
    internal static Task<BitmapSource?> CaptureAreaAsync(nint handle,System.Windows.Rect bounds) => Task.Run(() =>
    {
        using var sample=Sample(new[]{handle},(int)bounds.Left,(int)bounds.Top,Math.Max(1,(int)bounds.Width),Math.Max(1,(int)bounds.Height));
        if(sample is null)return null;
        var image=BitmapSource.Create(sample.Width,sample.Height,96,96,PixelFormats.Bgra32,null,Pixels(sample),sample.Width*4);image.Freeze();return image;
    });
    internal static Task<BackdropFrame?> CaptureSceneAsync(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        return Task.Run(() => CaptureScene(handle,false));
    }
    internal static BackdropFrame? CaptureHiddenScene(Window window)
    {
        if(window.IsVisible&&window.WindowState!=WindowState.Minimized)return null;
        return CaptureScene(new WindowInteropHelper(window).EnsureHandle(),true);
    }
    private static BackdropFrame? CaptureScene(nint handle,bool hidden)
    {
        if (handle == 0 || !Native.GetWindowRect(handle, out var windowBounds)) return null;
        var restoreMonitor = hidden ? Native.WindowMonitorBounds(handle) : null;
        var scene = SceneBounds(Rectangle.FromLTRB(windowBounds.Left, windowBounds.Top, windowBounds.Right, windowBounds.Bottom),
            Native.MonitorBounds().Select(monitor => Rectangle.FromLTRB(monitor.Left, monitor.Top, monitor.Right, monitor.Bottom)).ToArray(), hidden,
            restoreMonitor is { } monitor ? Rectangle.FromLTRB(monitor.Left, monitor.Top, monitor.Right, monitor.Bottom) : null);
        if (scene is null) return null;
        var bounds = scene.Value;
        using var sample = Sample(new[] { handle }, bounds.Left, bounds.Top, bounds.Width, bounds.Height);
        if (sample is null) return null;
        var image = BitmapSource.Create(sample.Width, sample.Height, 96, 96, PixelFormats.Bgra32, null, Pixels(sample), sample.Width * 4);
        image.Freeze();
        return new BackdropFrame(image, new System.Windows.Rect(bounds.Left, bounds.Top, bounds.Width, bounds.Height));
    }
    internal static Rectangle? SceneBounds(Rectangle window, IReadOnlyList<Rectangle> monitors, bool hidden, Rectangle? restoreMonitor = null)
    {
        if (monitors.Count == 0) return null;
        // Sampling the full touched monitors lets subsequent cross-screen drag positions reuse one frame.
        var touched = monitors.Where(monitor => monitor.IntersectsWith(window)).ToArray();
        if (touched.Length > 0) return touched.Aggregate(Rectangle.Union);
        if (!hidden) return null;
        if (restoreMonitor is { } fallback) return fallback;
        double Distance(Rectangle monitor)
        {
            double x = Math.Max(0, Math.Max(monitor.Left - (double)window.Right, window.Left - (double)monitor.Right));
            double y = Math.Max(0, Math.Max(monitor.Top - (double)window.Bottom, window.Top - (double)monitor.Bottom));
            return x * x + y * y;
        }
        return monitors.OrderBy(Distance).First();
    }
    private static Task<BitmapSource?> QueueCapture(Window window, bool client)
    {
        // Read the WPF handle on its owning thread; GDI sampling and bitmap conversion run off that thread.
        var handle = new WindowInteropHelper(window).Handle;
        return Task.Run(() => CaptureAtHandle(handle, client));
    }
    private static BitmapSource? CaptureAtHandle(nint handle, bool client)
    {
        if (handle == 0) return null;
        Native.Rect bounds;
        var origin = new ScreenPoint();
        if (client)
        {
            if (!GetClientRect(handle, out bounds) || !ClientToScreen(handle, ref origin)) return null;
        }
        else
        {
            if (!Native.GetWindowRect(handle, out bounds)) return null;
            origin.X = bounds.Left; origin.Y = bounds.Top;
        }
        if (bounds.Width <= 0 || bounds.Height <= 0) return null;
        using var sample = Sample(new[] { handle }, origin.X, origin.Y, bounds.Width, bounds.Height);
        if (sample is null) return null;
        var image = BitmapSource.Create(sample.Width, sample.Height, 96, 96, PixelFormats.Bgra32, null, Pixels(sample), sample.Width * 4);
        image.Freeze(); return image;
    }

    internal static BitmapSource? Capture(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        return CaptureAtHandle(handle, client: false);
    }

    internal static void Refresh(IReadOnlyList<ToastCard> cards)
    {
        if (cards.Count == 0) return;
        var windows = cards.Select(card => new WindowInteropHelper(card).Handle).ToArray();
        var bounds = new List<Native.Rect>();
        foreach (var window in windows)
        { if (!Native.GetWindowRect(window, out var rect) || rect.Width <= 0 || rect.Height <= 0) return; bounds.Add(rect); }
        const int padding = 16;
        int left = bounds.Min(r => r.Left) - padding, top = bounds.Min(r => r.Top) - padding;
        int width = bounds.Max(r => r.Right) - left + padding, height = bounds.Max(r => r.Bottom) - top + padding;
        using var sample = Sample(windows, left, top, width, height);
        if (sample is null) return;
        int smallWidth = sample.Width, smallHeight = sample.Height;
        var pixels = Pixels(sample);
        for (int i = 0; i < cards.Count; i++)
        {
            var rect = bounds[i]; int w = Math.Max(1, rect.Width / 2), h = Math.Max(1, rect.Height / 2);
            int ox = (rect.Left - left) / 2, oy = (rect.Top - top) / 2;
            var output = new int[w * h]; double radius = 28 * Native.GetDpiForWindow(windows[i]) / 192.0;
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
            {
                double cx = Math.Clamp(x, radius, Math.Max(radius, w - radius - 1));
                double cy = Math.Clamp(y, radius, Math.Max(radius, h - radius - 1));
                double nx = x - cx, ny = y - cy, length = Math.Sqrt(nx * nx + ny * ny), depth;
                if (length > .01) { nx /= length; ny /= length; depth = radius - length; }
                else
                {
                    double dx = Math.Min(x, w - 1 - x), dy = Math.Min(y, h - 1 - y);
                    if (dx < dy) { nx = x < w / 2 ? -1 : 1; ny = 0; depth = dx; }
                    else { nx = 0; ny = y < h / 2 ? -1 : 1; depth = dy; }
                }
                double bend = 2.4 * Math.Pow(Math.Max(0, 1 - Math.Max(0, depth) / 9), 2);
                int sx = Math.Clamp(ox + x - (int)Math.Round(nx * bend), 0, smallWidth - 1);
                int sy = Math.Clamp(oy + y - (int)Math.Round(ny * bend), 0, smallHeight - 1);
                output[y * w + x] = unchecked((int)0xFF000000) | (pixels[sy * smallWidth + sx] & 0xFFFFFF);
            }
            var image = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, output, w * 4);
            image.Freeze(); cards[i].SetBackdrop(image);
        }
    }

    private static Bitmap? Sample(nint[] windows, int left, int top, int width, int height)
    {
        try
        {
            using var full = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            var original = new uint[windows.Length]; int restoreCount = 0;
            try
            {
                // Exclude only our own windows during this in-memory sample.
                for (int i = 0; i < windows.Length; i++)
                {
                    if (!GetWindowDisplayAffinity(windows[i], out original[i])) return null;
                    restoreCount++;
                    if (!SetWindowDisplayAffinity(windows[i], 0x11)) return null;
                }
                using var graphics = Graphics.FromImage(full);
                graphics.CopyFromScreen(left, top, 0, 0, new System.Drawing.Size(width, height), CopyPixelOperation.SourceCopy);
            }
            finally { for (int i = 0; i < restoreCount; i++) SetWindowDisplayAffinity(windows[i], original[i]); }
            var small = new Bitmap(Math.Max(1, width / 2), Math.Max(1, height / 2), System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                using var graphics = Graphics.FromImage(small);
                graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
                graphics.DrawImage(full, new Rectangle(0, 0, small.Width, small.Height));
                return small;
            }
            catch { small.Dispose(); throw; }
        }
        catch (Exception) { return null; }
    }

    private static int[] Pixels(Bitmap bitmap)
    {
        var pixels = new int[bitmap.Width * bitmap.Height];
        var locked = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try { for (int y = 0; y < bitmap.Height; y++) Marshal.Copy(locked.Scan0 + y * locked.Stride, pixels, y * bitmap.Width, bitmap.Width); }
        finally { bitmap.UnlockBits(locked); }
        return pixels;
    }

    internal static void Blur(int[] pixels, int width, int height, int radius)
    {
        var scratch = new int[pixels.Length]; int count = radius * 2 + 1;
        for (int pass = 0; pass < 3; pass++)
        {
            for (int y = 0; y < height; y++)
            {
                int red = 0, green = 0, blue = 0;
                for (int k = -radius; k <= radius; k++) Add(pixels[y * width + Math.Clamp(k, 0, width - 1)], 1);
                for (int x = 0; x < width; x++)
                {
                    scratch[y * width + x] = unchecked((int)0xFF000000) | (red / count << 16) | (green / count << 8) | blue / count;
                    Add(pixels[y * width + Math.Clamp(x - radius, 0, width - 1)], -1);
                    Add(pixels[y * width + Math.Clamp(x + radius + 1, 0, width - 1)], 1);
                }
                void Add(int color, int sign) { red += ((color >> 16) & 255) * sign; green += ((color >> 8) & 255) * sign; blue += (color & 255) * sign; }
            }
            for (int x = 0; x < width; x++)
            {
                int red = 0, green = 0, blue = 0;
                for (int k = -radius; k <= radius; k++) Add(scratch[Math.Clamp(k, 0, height - 1) * width + x], 1);
                for (int y = 0; y < height; y++)
                {
                    pixels[y * width + x] = unchecked((int)0xFF000000) | (red / count << 16) | (green / count << 8) | blue / count;
                    Add(scratch[Math.Clamp(y - radius, 0, height - 1) * width + x], -1);
                    Add(scratch[Math.Clamp(y + radius + 1, 0, height - 1) * width + x], 1);
                }
                void Add(int color, int sign) { red += ((color >> 16) & 255) * sign; green += ((color >> 8) & 255) * sign; blue += (color & 255) * sign; }
            }
        }
    }
}
