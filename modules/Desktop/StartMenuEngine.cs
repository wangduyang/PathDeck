using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows.Threading;
using System.Windows.Media;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace TaskGlass;

internal sealed class StartMenuEngine : IDisposable
{
    internal const string GlobalConfiguration = "[Settings]\r\nExclude=*\r\nInclude=StartMenuExperienceHost.exe|SearchHost.exe\r\n";
    private readonly string folder = Path.Combine(Preferences.Root, "Engine", "StartMenuV2");
    private readonly DispatcherTimer monitor = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer appearanceUpdate = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private static int revision = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    private Preferences appearance = new();
    private string? appearanceError;
    private Process? process;
    private EventWaitHandle? stop;
    private DateTimeOffset started;
    internal event Action? Changed;
    internal bool Running => process is { HasExited: false };
    internal bool Loaded { get; private set; }
    internal bool SearchLoaded { get; private set; }
    internal string Status { get; private set; } = "开始与搜索样式未开启";

    internal StartMenuEngine()
    {
        monitor.Tick += (_, _) => ReadStatus();
        appearanceUpdate.Tick += (_, _) =>
        {
            appearanceUpdate.Stop();
            try { WriteAppearance(true); appearanceError = null; ReadStatus(); }
            catch (IOException error) { appearanceError = error.Message; SetStatus("外观更新失败：" + appearanceError); }
            catch (UnauthorizedAccessException error) { appearanceError = error.Message; SetStatus("外观更新失败：" + appearanceError); }
        };
    }

    internal static string ModConfiguration(bool enabled, Preferences settings)
    {
        var result = new StringBuilder("[Mod]\r\nLibraryFileName=TaskGlass.StartMenuStyler.dll\r\nArchitecture=x86-64\r\nInclude=StartMenuExperienceHost.exe|SearchHost.exe\r\n");
        result.Append("Disabled=").Append(enabled ? '0' : '1').Append("\r\nVersion=1.8\r\nSettingsChangeTime=")
            .Append(Interlocked.Increment(ref revision)).Append("\r\n\r\n[Settings]\r\n");
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("TaskGlass.StartMenu.PathDeck.json")!;
        var profile = JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
        string tint = GlassTheme.NormalizeColor(settings.TaskbarColor, GlassTheme.TaskbarTint);
        var material = GlassTheme.Material(tint, settings.Opacity);
        var color = material.Tint;
        var text = material.Foreground;
        bool dark = text == Colors.White;
        Color contrast = dark ? Colors.White : Colors.Black;
        Color worst = material.WorstBackground;
        Color Fill(Color value) => Color.FromRgb(
            dark ? Math.Min(value.R, worst.R) : Math.Max(value.R, worst.R),
            dark ? Math.Min(value.G, worst.G) : Math.Max(value.G, worst.G),
            dark ? Math.Min(value.B, worst.B) : Math.Max(value.B, worst.B));
        Color Ink(Color value)
        {
            for (int weight = 0; weight <= 100; weight++)
            {
                Color ink = GlassTheme.Mix(value, text, weight / 100.0);
                double ratio = dark ? (GlassTheme.Luminance(ink) + .05) / (GlassTheme.Luminance(worst) + .05)
                    : (GlassTheme.Luminance(worst) + .05) / (GlassTheme.Luminance(ink) + .05);
                if (ratio >= 4.7) return ink;
            }
            return text;
        }
        Color panel = Fill(GlassTheme.Mix(color, contrast, .06)), field = Fill(GlassTheme.Mix(color, contrast, .12));
        Color hover = Fill(GlassTheme.Mix(color, contrast, .18)), edge = GlassTheme.Mix(color, contrast, .3);
        Color muted = Ink(GlassTheme.Mix(color, text, .72));
        var accent = Ink((Color)ColorConverter.ConvertFromString(dark ? "#9EDCC5" : "#26634C"));
        Color selected = Fill(GlassTheme.Mix(color, dark ? accent : Color.FromRgb(193, 235, 217), .23));
        string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
        string Hex(Color value) => $"#{value.R:X2}{value.G:X2}{value.B:X2}";
        string Alpha(Color value, byte alpha) => $"#{alpha:X2}{value.R:X2}{value.G:X2}{value.B:X2}";
        string Rgb(Color value) => $"{value.R},{value.G},{value.B}";
        string backdrop = $"<WindhawkBlur BlurAmount=\"{Number(Math.Clamp(settings.Blur, 0, 100) * .6)}\" TintColor=\"{tint}\" TintOpacity=\"{Number(material.TintOpacity)}\" TintLuminosityOpacity=\"{Number(material.LuminosityOpacity)}\" TintSaturation=\"{Number(GlassMaterial.Saturation)}\" NoiseOpacity=\"0.006\" FallbackColor=\"{tint}\"/>";
        var tokens = new Dictionary<string, string>
        {
            ["{{Backdrop}}"] = backdrop, ["{{Tint}}"] = tint,
            ["{{Theme}}"] = dark ? "2" : "1", ["{{Scheme}}"] = dark ? "dark" : "light",
            ["{{Text}}"] = Hex(text), ["{{Muted}}"] = Hex(muted),
            ["{{Accent}}"] = Hex(accent), ["{{AccentLight}}"] = Hex(Ink(GlassTheme.Mix(accent, Colors.White, .35))),
            ["{{AccentDark}}"] = Hex(Ink(GlassTheme.Mix(accent, Colors.Black, .2))),
            ["{{Selected}}"] = Hex(selected), ["{{SelectionHover}}"] = Hex(Fill(GlassTheme.Mix(selected, contrast, .08))),
            ["{{HoverAlpha}}"] = Alpha(hover, 56), ["{{Panel}}"] = Alpha(panel, 30),
            ["{{Edge}}"] = Alpha(edge, 72), ["{{SurfaceEdge}}"] = Alpha(edge, 72),
            ["{{Footer}}"] = Alpha(panel, 26),
            ["{{FieldTop}}"] = Alpha(field, 54), ["{{FieldBottom}}"] = Alpha(panel, 26), ["{{Field}}"] = Alpha(field, 40),
            ["{{PanelRgb}}"] = Rgb(panel), ["{{FieldRgb}}"] = Rgb(field), ["{{HoverRgb}}"] = Rgb(hover),
            ["{{EdgeRgb}}"] = Rgb(edge), ["{{TextRgb}}"] = Rgb(text), ["{{Scrollbar}}"] = Hex(muted)
        };
        foreach (var pair in profile)
        {
            string value = pair.Value;
            foreach (var token in tokens) value = value.Replace(token.Key, token.Value, StringComparison.Ordinal);
            result.Append(pair.Key).Append('=').Append(value).Append("\r\n");
        }
        return result.ToString();
    }

    internal void Configure(Preferences settings)
    {
        bool same = appearance.TaskbarColor == settings.TaskbarColor && appearance.Blur == settings.Blur && appearance.Opacity == settings.Opacity;
        appearance = settings;
        if (!settings.StyleStartMenu) { Stop(); return; }
        if (Running)
        {
            if (!same || appearanceError is not null) { appearanceUpdate.Stop(); appearanceUpdate.Start(); }
            return;
        }
        Stop();
        try
        {
            string executable = Path.Combine(folder, "TaskGlass.StartHost.exe");
            if (!File.Exists(executable) || !File.Exists(Path.Combine(folder, "Data", "Mods", "64", "TaskGlass.StartMenuStyler.dll")))
                throw new FileNotFoundException("内置开始菜单组件缺失");
            Directory.CreateDirectory(Path.Combine(folder, "Data", "Mods"));
            File.WriteAllText(Path.Combine(folder, "Data", "settings.ini"), GlobalConfiguration, Encoding.Unicode);
            WriteAppearance(true);
            string eventName = @"Local\TaskGlass.StartMenu.Stop." + Environment.ProcessId;
            stop = new EventWaitHandle(false, EventResetMode.ManualReset, eventName);
            process = Process.Start(new ProcessStartInfo(executable)
            {
                WorkingDirectory = folder, UseShellExecute = false, CreateNoWindow = true,
                ArgumentList = { Environment.ProcessId.ToString(), eventName }
            }) ?? throw new InvalidOperationException("无法启动内置开始菜单组件");
            started = DateTimeOffset.UtcNow;
            SetStatus("正在连接 Windows 开始菜单…");
            monitor.Start();
        }
        catch (Exception error)
        {
            stop?.Dispose(); stop = null;
            SetStatus("开始与搜索样式不可用：" + error.Message);
        }
    }

    private string ModPath => Path.Combine(folder, "Data", "Mods", "windows-11-start-menu-styler.ini");

    private void WriteAppearance(bool enabled)
    {
        string temporary = ModPath + ".new";
        File.WriteAllText(temporary, ModConfiguration(enabled, appearance), Encoding.Unicode);
        File.Move(temporary, ModPath, true);
    }

    private void ReadStatus()
    {
        if (!Running)
        {
            monitor.Stop(); Loaded = false; SearchLoaded = false;
            SetStatus("开始菜单组件已停止" + (process is null ? "" : $"（{process.ExitCode}）"));
            return;
        }
        bool loaded = false;
        bool searchLoaded = false;
        try
        {
            using var key = OpenStatus();
            if (key is not null)
                foreach (string name in key.GetValueNames())
                {
                    if (!name.EndsWith("_windows-11-start-menu-styler", StringComparison.Ordinal) ||
                        !int.TryParse(name.Split('_')[0], out int pid) || key.GetValue(name) is not string value) continue;
                    string[] fields = value.Split('|', 4);
                    if (fields.Length != 4 || fields[3] != "Loaded") continue;
                    try
                    {
                        using var target = Process.GetProcessById(pid);
                        if (target.HasExited || target.StartTime.ToFileTimeUtc().ToString() != fields[1]) continue;
                        if (fields[2] == "StartMenuExperienceHost.exe") loaded = true;
                        if (fields[2] == "SearchHost.exe") searchLoaded = true;
                    }
                    catch (ArgumentException) { }
                    catch (InvalidOperationException) { }
                    catch (Win32Exception) { }
                }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        Loaded = loaded;
        SearchLoaded = searchLoaded;
        SetStatus(appearanceError is not null ? "外观更新失败：" + appearanceError : loaded && searchLoaded ? "开始与搜索已连接 · 保留原始图标" : loaded ? "开始菜单已连接 · 搜索等待连接" : searchLoaded ? "搜索已连接 · 开始菜单等待连接" :
            DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(30) ? "正在连接 Windows 开始菜单…" : "尚未连接开始菜单，请打开开始菜单后重试");
    }

    private RegistryKey? OpenStatus()
    {
        string session = process!.Id + "_" + process.StartTime.ToFileTimeUtc();
        string category = session + @"\mod-status";
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        RegistryKey? key = machine.OpenSubKey(@"SOFTWARE\WindhawkSessions\" + category);
        if (key is not null) return key;
        string path = Path.Combine(folder, "Data", "Sessions", session + ".hiv");
        if (!File.Exists(path)) return null;
        int error = RegLoadAppKey(path, out SafeRegistryHandle handle, 0x20019, 0, 0);
        using (handle)
        {
            if (error != 0) return null;
            using var hive = RegistryKey.FromHandle(handle);
            return hive.OpenSubKey(category);
        }
    }

    [DllImport("advapi32.dll", EntryPoint = "RegLoadAppKeyW", CharSet = CharSet.Unicode)]
    private static extern int RegLoadAppKey(string file, out SafeRegistryHandle key, int access, int options, int reserved);

    private void SetStatus(string value)
    {
        if (Status == value) return;
        Status = value; Changed?.Invoke();
    }

    private void Stop()
    {
        appearanceUpdate.Stop(); appearanceError = null;
        monitor.Stop();
        if (process is not null)
        {
            try
            {
                try { if (File.Exists(ModPath)) WriteAppearance(false); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                finally
                {
                    stop?.Set();
                    if (!process.HasExited && !process.WaitForExit(5000)) { process.Kill(); process.WaitForExit(1000); }
                }
            }
            finally { process.Dispose(); process = null; stop?.Dispose(); stop = null; }
        }
        Loaded = false;
        SearchLoaded = false;
        SetStatus("开始与搜索样式未开启");
    }

    public void Dispose() => Stop();
}
