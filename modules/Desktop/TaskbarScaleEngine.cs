using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Threading;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace TaskGlass;

// Separate session: taskbar sizing only runs inside Explorer, never the Start/Search hosts.
internal sealed class TaskbarScaleEngine : IDisposable
{
    internal const string GlobalConfiguration = "[Settings]\r\nExclude=*\r\nInclude=explorer.exe\r\n";
    private readonly string folder = Path.Combine(Preferences.Root, "Engine", "TaskbarScale");
    private readonly DispatcherTimer monitor = new() { Interval = TimeSpan.FromSeconds(1) };
    private Process? process;
    private EventWaitHandle? stop;
    private static int revision = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    internal event Action? Changed;
    // Fixed from the measured 48-DIP baseline; restarts must never multiply an already-scaled bar.
    internal int Height => 72;
    internal int IconSize => 36;
    internal int ButtonWidth => 66;
    internal bool Running => process is { HasExited: false };
    internal bool Applied { get; private set; }
    internal bool Loaded { get; private set; }
    internal string Status { get; private set; } = "任务栏尺寸组件未启动";

    internal TaskbarScaleEngine() => monitor.Tick += (_, _) => ReadStatus();

    internal static int TargetPhysicalHeight(uint dpi) =>
        (int)Math.Round(72 * Math.Max(1, dpi / 96.0), MidpointRounding.AwayFromZero);

    internal static string ModConfiguration(bool enabled, int height) =>
        "[Mod]\r\nLibraryFileName=TaskGlass.TaskbarScale.dll\r\nArchitecture=x86-64\r\nInclude=explorer.exe\r\n" +
        "Disabled=" + (enabled ? "0" : "1") + "\r\nVersion=1.3.11\r\nSettingsChangeTime=" + Interlocked.Increment(ref revision) +
        "\r\n\r\n[Settings]\r\nTaskbarHeight=" + height + "\r\nIconSize=36\r\nTaskbarButtonWidth=66\r\nIconSizeSmall=24\r\nTaskbarButtonWidthSmall=48\r\n";

    internal void Start()
    {
        if (Running) return;
        try
        {
            string executable = Path.Combine(folder, "TaskGlass.TaskbarHost.exe");
            if (!File.Exists(executable) || !File.Exists(Path.Combine(folder, "Data", "Mods", "64", "TaskGlass.TaskbarScale.dll")))
                throw new FileNotFoundException("内置任务栏尺寸组件缺失");
            Directory.CreateDirectory(Path.Combine(folder, "Data", "Mods"));
            File.WriteAllText(Path.Combine(folder, "Data", "settings.ini"), GlobalConfiguration, Encoding.Unicode);
            WriteConfiguration(true);
            string eventName = @"Local\TaskGlass.TaskbarScale.Stop." + Environment.ProcessId;
            stop = new EventWaitHandle(false, EventResetMode.ManualReset, eventName);
            process = Process.Start(new ProcessStartInfo(executable)
            {
                WorkingDirectory = folder, UseShellExecute = false, CreateNoWindow = true,
                ArgumentList = { Environment.ProcessId.ToString(), eventName }
            }) ?? throw new InvalidOperationException("无法启动内置任务栏尺寸组件");
            SetStatus("正在调整任务栏高度与图标…");
            monitor.Start(); ReadStatus();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
        {
            Stop(); SetStatus("任务栏尺寸组件：" + error.Message);
        }
    }
    private void WriteConfiguration(bool enabled)
    {
        string path = Path.Combine(folder, "Data", "Mods", "taskglass-taskbar-scale.ini");
        File.WriteAllText(path + ".new", ModConfiguration(enabled, Height), Encoding.Unicode);
        File.Move(path + ".new", path, true);
    }
    private void ReadStatus()
    {
        bool previous = Applied;
        Loaded = Running && ReadLoaded();
        Applied = Running && Loaded;
        var windows = Native.Taskbars();
        if (windows.Count == 0) Applied = false;
        foreach (var window in windows)
            if (!Native.GetWindowRect(window, out var bounds) || Math.Abs(bounds.Height - TargetPhysicalHeight(Native.GetDpiForWindow(window))) > Math.Max(1, Native.GetDpiForWindow(window) / 96.0))
                Applied = false;
        SetStatus(!Running ? "任务栏尺寸组件已退出" : Applied ? "任务栏高度与图标已放大 1.5 倍" : "等待原生任务栏尺寸更新…");
        if (previous != Applied) Changed?.Invoke();
    }
    private void SetStatus(string value)
    { if (Status == value) return; Status = value; Changed?.Invoke(); }
    private bool ReadLoaded()
    {
        try
        {
            using var key = OpenStatus();
            if (key is null) return false;
            foreach (string name in key.GetValueNames())
            {
                if (!name.EndsWith("_taskglass-taskbar-scale", StringComparison.Ordinal) ||
                    !int.TryParse(name.Split('_')[0], out int pid) || key.GetValue(name) is not string value) continue;
                string[] fields = value.Split('|', 4);
                if (fields.Length != 4 || fields[2] != "explorer.exe" || fields[3] != "Loaded") continue;
                bool ownsTaskbar = false;
                foreach (var window in Native.Taskbars())
                { Native.GetWindowThreadProcessId(window, out uint owner); if (owner == pid) ownsTaskbar = true; }
                if (!ownsTaskbar) continue;
                using var target = Process.GetProcessById(pid);
                if (!target.HasExited && target.StartTime.ToFileTimeUtc().ToString() == fields[1]) return true;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or Win32Exception) { }
        return false;
    }
    private RegistryKey? OpenStatus()
    {
        string session = process!.Id + "_" + process.StartTime.ToFileTimeUtc();
        string category = session + @"\mod-status";
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        var key = machine.OpenSubKey(@"SOFTWARE\WindhawkSessions\" + category);
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
    private void Stop()
    {
        monitor.Stop();
        if (process is not null)
        {
            try
            {
                try { WriteConfiguration(false); }
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
        Applied = false; Loaded = false;
    }
    public void Dispose() => Stop();
}
