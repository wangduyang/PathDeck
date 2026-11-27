using System;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Microsoft.Win32;

namespace TaskGlass;

internal sealed record Preferences(double Opacity = 45, double Radius = 24, bool ReadWeChatUnread = false, double Blur = 60)
{
    public string TaskbarColor { get; init; } = GlassTheme.TaskbarTint;
    public string NotificationColor { get; init; } = GlassTheme.NotificationTint;
    public string DesktopColor { get; init; } = GlassTheme.TaskbarTint;
    public bool StyleStartMenu { get; init; } = true;
    public bool AnimationEnabled { get; init; } = true;
    public bool ShowStatusBar { get; init; } = true;
    public bool LauncherEnabled { get; init; } = true;
    public bool ControlCenterEnabled { get; init; } = true;
    public bool LauncherFullscreen { get; init; }
    public string LauncherHotkey { get; init; } = "Ctrl+Alt+Space";
    public bool ClockSeconds { get; init; }
    public string WorkStart { get; init; } = "08:30";
    public string WorkEnd { get; init; } = "17:50";
    public bool LunarDate { get; init; } = true;
    public string StatusMonitor { get; init; } = "primary";
    public string[] StatusComponents { get; init; } = new[] { "Launcher", "Clock", "Resources", "Input", "Notifications", "Tray", "Controls" };
    public string PerformancePreset { get; init; } = "均衡";
    internal static readonly string[] ComponentNames = { "Launcher", "Clock", "Resources", "Input", "Notifications", "Tray", "Controls" };
    internal static string Root => Path.Combine(AppContext.BaseDirectory, "Modules", "Desktop");
    internal static string FilePath => Path.Combine(Root, "settings.json");
    internal static Preferences Load()
    {
        Directory.CreateDirectory(Root);
        var value = File.Exists(FilePath) ? JsonSerializer.Deserialize<Preferences>(File.ReadAllText(FilePath)) ?? new() : new();
        return Normalize(value);
    }
    internal static Preferences Normalize(Preferences value)
    {
        bool workHoursValid=TryWorkHours(value.WorkStart,value.WorkEnd,out _,out _);
        return value with
        {
            Opacity = double.IsFinite(value.Opacity) ? Math.Clamp(value.Opacity, 10, 80) : 45,
            Radius = double.IsFinite(value.Radius) ? Math.Clamp(value.Radius, 0, 24) : 24,
            Blur = double.IsFinite(value.Blur) ? Math.Clamp(value.Blur, 0, 100) : 60,
            TaskbarColor = GlassTheme.NormalizeColor(value.TaskbarColor, GlassTheme.TaskbarTint),
            NotificationColor = GlassTheme.NormalizeColor(value.TaskbarColor, GlassTheme.TaskbarTint),
            DesktopColor = GlassTheme.NormalizeColor(value.TaskbarColor, GlassTheme.TaskbarTint),
            LauncherHotkey = TaskGlass.LauncherHotkey.IsValid(value.LauncherHotkey) ? value.LauncherHotkey : "Ctrl+Alt+Space",
            PerformancePreset = value.PerformancePreset is "节能" or "均衡" or "完整效果" ? value.PerformancePreset : "均衡",
            StatusMonitor = string.IsNullOrWhiteSpace(value.StatusMonitor) ? "primary" : value.StatusMonitor,
            WorkStart = workHoursValid ? value.WorkStart : "08:30",
            WorkEnd = workHoursValid ? value.WorkEnd : "17:50",
            StatusComponents = (value.StatusComponents ?? ComponentNames).Where(ComponentNames.Contains).Distinct().ToArray()
        };
    }
    internal static bool TryWorkHours(string? start,string? end,out TimeOnly from,out TimeOnly to)
    {
        bool validStart=TimeOnly.TryParseExact(start,"HH:mm",CultureInfo.InvariantCulture,DateTimeStyles.None,out from);
        bool validEnd=TimeOnly.TryParseExact(end,"HH:mm",CultureInfo.InvariantCulture,DateTimeStyles.None,out to);
        return validStart&&validEnd&&to>from;
    }
    internal void Save()
    { Directory.CreateDirectory(Root); string temporary = FilePath + ".new"; File.WriteAllText(temporary, JsonSerializer.Serialize(this)); File.Move(temporary, FilePath, true); }
}

internal static class Startup
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal static string Command => $"\"{ModuleNavigation.Executable}\" --background";
    internal static bool Enabled
    {
        get { using var key = Registry.CurrentUser.OpenSubKey(Key); return Equals(key?.GetValue("PathDeck"), Command); }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(Key);
            if (value) key.SetValue("PathDeck", Command, RegistryValueKind.String);
            else key.DeleteValue("PathDeck", false);
        }
    }
}
