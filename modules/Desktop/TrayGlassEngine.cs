using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace TaskGlass;

// A second module owned by the existing Explorer-only TaskbarScale session.
// Only the native overflow backdrop changes; Windows retains every tray icon and input path.
internal sealed class TrayGlassEngine : IDisposable
{
    private readonly string folder = Path.Combine(Preferences.Root, "Engine", "TaskbarScale");
    private static int revision = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    private Preferences? appearance;
    private bool disposed;
    internal string Status { get; private set; } = "托盘玻璃未配置";

    internal static string ModConfiguration(bool enabled, Preferences settings)
    {
        string tint = GlassTheme.NormalizeColor(settings.TaskbarColor, GlassTheme.TaskbarTint);
        var material = GlassTheme.Material(tint, settings.Opacity);
        string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
        string backdrop = $"<WindhawkBlur BlurAmount=\"{Number(Math.Clamp(settings.Blur, 0, 100) * .6)}\" TintColor=\"{tint}\" TintOpacity=\"{Number(material.TintOpacity)}\" TintLuminosityOpacity=\"{Number(material.LuminosityOpacity)}\" TintSaturation=\"{Number(GlassMaterial.Saturation)}\" NoiseOpacity=\"0.006\" FallbackColor=\"{tint}\"/>";
        return "[Mod]\r\nLibraryFileName=TaskGlass.TrayGlass.dll\r\nArchitecture=x86-64\r\nInclude=explorer.exe\r\n" +
            "Disabled=" + (enabled ? "0" : "1") + "\r\nVersion=1.11.2\r\nSettingsChangeTime=" + Interlocked.Increment(ref revision) +
            "\r\n\r\n[Settings]\r\ntheme=\r\nclickThroughTaskbar=0\r\nxamlDiagnosticsHandling=allow\r\n" +
            "controlStyles[0].target=SystemTray.NotificationAreaOverflow\r\n" +
            "controlStyles[0].styles[0]=Background=Transparent\r\n" +
            "controlStyles[0].styles[1]=CornerRadius=14\r\n" +
            "controlStyles[1].target=SystemTray.NotificationAreaOverflow > Grid#OverflowRootGrid\r\n" +
            "controlStyles[1].styles[0]=Background=Transparent\r\n" +
            "controlStyles[1].styles[1]=CornerRadius=14\r\n" +
            "controlStyles[2].target=SystemTray.NotificationAreaOverflow > Grid#OverflowRootGrid > Border#OverflowFlyoutBackgroundBorder, SystemTray.NotificationAreaOverflow > Grid#OverflowRootGrid > Border#OverflowFlyoutBackground\r\n" +
            "controlStyles[2].styles[0]=Background:=" + backdrop + "\r\n" +
            "controlStyles[2].styles[1]=CornerRadius=14\r\n" +
            "controlStyles[2].styles[2]=BorderThickness=0\r\n";
    }

    internal void Configure(Preferences settings)
    {
        if (disposed) return;
        if (appearance is not null && appearance.TaskbarColor == settings.TaskbarColor &&
            appearance.Opacity == settings.Opacity && appearance.Blur == settings.Blur && File.Exists(ModPath)) return;
        try
        {
            WriteConfiguration(true, settings);
            appearance = settings;
            Status = "原生托盘玻璃已配置";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { Status = "托盘玻璃配置失败：" + error.Message; }
    }

    private string ModPath => Path.Combine(folder, "Data", "Mods", "taskglass-tray-glass.ini");
    private void WriteConfiguration(bool enabled, Preferences settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ModPath)!);
        File.WriteAllText(ModPath + ".new", ModConfiguration(enabled, settings), Encoding.Unicode);
        File.Move(ModPath + ".new", ModPath, true);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (appearance is null) return;
        try { WriteConfiguration(false, appearance); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { Status = "托盘玻璃停止失败：" + error.Message; }
    }
}
