using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using TaskGlass;

namespace QuickFileDesk;

// The desktop module is the only writer. File management follows the same saved appearance.
internal sealed class SharedAppearance : IDisposable
{
    private readonly App app;
    private readonly DispatcherTimer watcher = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private DateTime lastWrite;
    internal Preferences Preferences { get; private set; } = new();
    internal event Action? Changed;

    internal SharedAppearance(App app)
    {
        this.app = app;
        Refresh();
        watcher.Tick += (_, _) => Refresh();
        watcher.Start();
    }

    private void Refresh()
    {
        try
        {
            var stamp = File.GetLastWriteTimeUtc(TaskGlass.Preferences.FilePath);
            if (lastWrite == stamp) return;
            var preferences = TaskGlass.Preferences.Load();
            Apply(preferences);
            lastWrite = stamp;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            // Atomic settings replacement may briefly overlap a read; keep the current material and retry.
        }
    }

    internal void Apply(Preferences preferences)
    {
        Preferences = preferences;
        var material = GlassTheme.Material(preferences.TaskbarColor, preferences.Opacity);
        bool dark = material.Foreground == Colors.White;
        app.ApplySharedPalette(dark);
        Brush Surface(byte alpha) => new SolidColorBrush(Color.FromArgb(alpha, material.Tint.R, material.Tint.G, material.Tint.B));
        foreach (string key in new[] { "Chrome", "Panel", "SidebarCard", "Field", "ToolbarSurface", "OverviewTile", "SearchSurface" })
            app.Resources[key] = Surface(key is "Panel" or "SidebarCard" ? (byte)16 : (byte)10);
        app.Resources["WindowBackground"] = material.Fallback;
        app.Resources["Sidebar"] = Brushes.Transparent;
        app.Resources["Text"] = new SolidColorBrush(material.Foreground);
        // Secondary text shares the contrast-safe ink; size and placement carry the hierarchy.
        app.Resources["Muted"] = new SolidColorBrush(material.Foreground);
        // Keep the broad moving reflection on the readable side of the ink; the rim stays bright.
        var reflection = dark ? material.Tint : Colors.White;
        app.Resources["GlassHighlightColor"] = Color.FromArgb(150, reflection.R, reflection.G, reflection.B);
        app.Resources["GlassSheen"] = new LinearGradientBrush(Color.FromArgb(26,reflection.R,reflection.G,reflection.B), Colors.Transparent, 90);
        app.Resources["GlassEdge"] = GlassTheme.Rim();
        foreach (Window window in app.Windows) WindowsIntegration.SetWindowFrameTheme(window, dark);
        Changed?.Invoke();
    }

    public void Dispose() => watcher.Stop();
}
