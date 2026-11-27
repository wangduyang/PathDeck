using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace TaskGlass;

internal static class Program
{
    [STAThread] internal static void Main(string[] args)
    {
        if (args.Contains("--stop")) { Signal("Exit"); return; }
        if (args.Contains("--test-notification")) { Signal("Test"); return; }
        using var mutex = new Mutex(true, @"Local\TaskGlass.Instance", out bool first);
        if (!first) { if (args.Contains("--launchpad")) Signal("Launcher"); else if (args.Contains("--control-center")) Signal("Controls"); else if (!args.Contains("--background")) Signal("Show"); return; }
        try { new GlassApplication(args).Run(); }
        finally { mutex.ReleaseMutex(); }
    }
    private static void Signal(string action)
    {
        try { using var signal = EventWaitHandle.OpenExisting(@"Local\TaskGlass." + action); signal.Set(); }
        catch (WaitHandleCannotBeOpenedException) { }
    }
}

internal sealed class GlassApplication : System.Windows.Application
{
    private readonly string[] args;
    private readonly MaterialEngine engine = new();
    private readonly StartMenuEngine startMenu = new();
    private readonly TaskbarScaleEngine taskbarScale = new();
    private readonly TrayGlassEngine trayGlass = new();
    private TaskbarController? taskbars;
    private Preferences preferences = new();
    private SettingsWindow? settings;
    private Forms.NotifyIcon? tray;
    private Icon? icon;
    private NotificationHub? notifications;
    private WindowsNoticeSource? windowsNotices;
    private ClientPopupSource? clientNotices;
    private WeChatUnreadSource? weChatNotices;
    private WeChatSignalSource? weChatSignals;
    private DesktopRegionController? desktop;
    private LauncherController? launcher;
    private LauncherHotkey? hotkey;
    private StatusBarController? statusBar;
    private ControlCenterWindow? controlCenter;
    private string moduleError = "";
    private readonly EventWaitHandle launcherEvent = new(false, EventResetMode.AutoReset, @"Local\TaskGlass.Launcher");
    private readonly EventWaitHandle controlsEvent = new(false, EventResetMode.AutoReset, @"Local\TaskGlass.Controls");
    private RegisteredWaitHandle? launcherWait, controlsWait;
    private readonly EventWaitHandle show = new(false, EventResetMode.AutoReset, @"Local\TaskGlass.Show");
    private readonly EventWaitHandle exit = new(false, EventResetMode.AutoReset, @"Local\TaskGlass.Exit");
    private readonly EventWaitHandle test = new(false, EventResetMode.AutoReset, @"Local\TaskGlass.Test");
    private RegisteredWaitHandle? showWait, exitWait, testWait;
    internal bool Exiting { get; private set; }
    internal Preferences Preferences => preferences;
    internal string State { get; private set; } = "正在连接任务栏…";

    internal GlassApplication(string[] args)
    {
        this.args = args;
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        DispatcherUnhandledException += (_, e) =>
        {
            File.WriteAllText(Path.Combine(Preferences.Root, "last-error.txt"), e.Exception.ToString());
            e.Handled = true; Shutdown(1);
        };
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        GlassMenus.Install(this);
        showWait = ThreadPool.RegisterWaitForSingleObject(show, (_, _) => Dispatcher.BeginInvoke(ShowSettings), null, -1, false);
        exitWait = ThreadPool.RegisterWaitForSingleObject(exit, (_, _) => Dispatcher.BeginInvoke(() => Shutdown()), null, -1, false);
        testWait = ThreadPool.RegisterWaitForSingleObject(test, (_, _) => Dispatcher.BeginInvoke(TestNotification), null, -1, false);
        launcherWait = ThreadPool.RegisterWaitForSingleObject(launcherEvent, (_, _) => Dispatcher.BeginInvoke(ShowLauncher), null, -1, false);
        controlsWait = ThreadPool.RegisterWaitForSingleObject(controlsEvent, (_, _) => Dispatcher.BeginInvoke(ShowControlCenter), null, -1, false);
        try
        {
            preferences = Preferences.Load();
            GlassMotion.Configure(preferences.AnimationEnabled && preferences.PerformancePreset != "节能");
            engine.Start(preferences);
            await Task.Delay(1400);
            if (Exiting) return;
            if (!engine.Running) throw new InvalidOperationException("任务栏材质引擎未能启动，请查看 Engine 目录内的日志。");
            taskbarScale.Changed += () => { if (!Exiting) WriteStatus(); };
            trayGlass.Configure(preferences);
            taskbarScale.Start();
            taskbars = new TaskbarController(preferences);
            taskbars.LayoutChanged += () => { if (!Exiting) WriteStatus(); };
            if (args.Contains("--enable-startup")) TaskGlass.Startup.Enabled = true;
            State = $"运行中 · {taskbars.Count} 个任务栏";
            Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/PathDeck.Desktop;component/NotificationStyles.xaml", UriKind.Relative) });
            notifications = new NotificationHub();
            notifications.SetBlur(preferences.Blur);
            notifications.SetColor(preferences.TaskbarColor);
            notifications.SetMaterialOpacity(preferences.Opacity);
            desktop = new DesktopRegionController();
            desktop.SetAppearance(preferences);
            desktop.Changed += () => { if (!Exiting) { settings?.ReadDesktopStatus(); WriteStatus(); } };
            icon = CreateIcon();
            var menu = new System.Windows.Controls.ContextMenu();
            System.Windows.Controls.MenuItem MenuAction(string title,Action action)
            {
                var item=new System.Windows.Controls.MenuItem {Header=title};
                item.Click+=(_,_)=>action();return item;
            }
            menu.Items.Add(MenuAction("打开文件管理器",()=>ModuleNavigation.OpenFiles()));
            var tools=new System.Windows.Controls.MenuItem {Header="快捷工具"};
            var launchItem=MenuAction("启动台",ShowLauncher);var controlsItem=MenuAction("控制中心",ShowControlCenter);
            tools.Items.Add(launchItem);tools.Items.Add(controlsItem);menu.Items.Add(tools);
            menu.Items.Add(new System.Windows.Controls.Separator());
            menu.Items.Add(MenuAction("桌面与任务栏设置…",ShowSettings));
            var startup=new System.Windows.Controls.MenuItem {Header="开机启动",IsCheckable=true};
            startup.Click+=(_,_)=>{TaskGlass.Startup.Enabled=startup.IsChecked;settings?.ReadStartup();WriteStatus();};
            menu.Items.Add(startup);menu.Items.Add(new System.Windows.Controls.Separator());
            var quit=MenuAction("退出",()=>Shutdown());quit.ToolTip="退出桌面增强并恢复 Windows 外观";menu.Items.Add(quit);
            tray = new Forms.NotifyIcon { Icon = icon, Text = "PathDeck", Visible = true };
            tray.MouseUp+=(_,mouse)=>
            {
                if(mouse.Button!=Forms.MouseButtons.Right)return;
                startup.IsChecked=TaskGlass.Startup.Enabled;
                launchItem.Visibility=preferences.LauncherEnabled?Visibility.Visible:Visibility.Collapsed;
                controlsItem.Visibility=preferences.ControlCenterEnabled?Visibility.Visible:Visibility.Collapsed;
                tools.Visibility=preferences.LauncherEnabled||preferences.ControlCenterEnabled?Visibility.Visible:Visibility.Collapsed;
                menu.Placement=System.Windows.Controls.Primitives.PlacementMode.MousePoint;menu.IsOpen=true;menu.Focus();
            };
            tray.DoubleClick += (_, _) => Dispatcher.BeginInvoke(ShowSettings);
            notifications.Changed += () =>
            {
                if (Exiting) return;
                settings?.ReadNotificationStatus(); WriteStatus();
            };
            void Publish(Notice notice) => Dispatcher.BeginInvoke(() => { if (!Exiting) notifications.Post(notice); });
            void Report(bool system, string status) => Dispatcher.BeginInvoke(() => { if (!Exiting) notifications.SetStatus(system, status); });
            windowsNotices = new WindowsNoticeSource(Publish, value => Report(true, value));
            clientNotices = new ClientPopupSource(Publish, value => Report(false, value));
            SetWeChatSource();
            weChatSignals = new WeChatSignalSource(Publish, () => weChatNotices?.Available != true);
            weChatSignals.Changed += () => { if (!Exiting) WriteStatus(); };
            startMenu.Changed += () => { if (!Exiting) { settings?.ReadStartMenuStatus(); WriteStatus(); } };
            startMenu.Configure(preferences);
            launcher = new LauncherController(() => preferences, text => { moduleError = text; WriteStatus(); });
            launcher.Changed += () => { if (!Exiting) WriteStatus(); };
            hotkey = new LauncherHotkey(ShowLauncher); if (preferences.LauncherEnabled) hotkey.Configure(preferences.LauncherHotkey); else hotkey.Disable();
            statusBar = new StatusBarController(() => preferences, ShowLauncher, ShowControlCenterAt, ShowSettings, () => notifications.Store.Items.Count, () => notifications.Reveal());
            statusBar.Update();
            WriteStatus();
            _ = Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                if (!Exiting && settings is null)
                {
                    settings = new SettingsWindow(this);
                    settings.Measure(new System.Windows.Size(settings.Width, settings.Height));
                    settings.Arrange(new Rect(0, 0, settings.Width, settings.Height));
                    settings.UpdateLayout();
                    new System.Windows.Interop.WindowInteropHelper(settings).EnsureHandle();
                }
            }));
            if (args.Contains("--launchpad")) ShowLauncher(); else if (args.Contains("--control-center")) ShowControlCenter(); else if (!args.Contains("--background")) ShowSettings();
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(Preferences.Root, "last-error.txt"), error.ToString());
            System.Windows.MessageBox.Show(error.Message, "PathDeck", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(1);
        }
    }

    internal void Update(Preferences value)
    {
        value = Preferences.Normalize(value);
        bool changed = value.ReadWeChatUnread != preferences.ReadWeChatUnread;
        bool shortcutChanged = value.LauncherHotkey != preferences.LauncherHotkey || value.LauncherEnabled != preferences.LauncherEnabled;
        value.Save(); engine.Configure(value); taskbars?.Update(value); preferences = value;
        GlassMotion.Configure(value.AnimationEnabled && value.PerformancePreset != "节能");
        notifications?.SetBlur(value.Blur); settings?.SetBlur(value.Blur);
        notifications?.SetMaterialOpacity(value.Opacity);
        notifications?.SetColor(value.TaskbarColor); desktop?.SetAppearance(value); settings?.ReadColors();
        if (changed) SetWeChatSource();
        startMenu.Configure(value);
        trayGlass.Configure(value);
        launcher?.Update(value); controlCenter?.ApplyAppearance(value); statusBar?.Update();
        if (!value.ControlCenterEnabled) controlCenter?.Dismiss();
        if (shortcutChanged) { if (value.LauncherEnabled) hotkey?.Configure(value.LauncherHotkey); else hotkey?.Disable(); }
        settings?.ReadModules();
        WriteStatus();
    }

    private void SetWeChatSource()
    {
        weChatNotices?.Dispose(); weChatNotices = null;
        if (!preferences.ReadWeChatUnread) { notifications?.SetWeChatStatus("微信未读摘要未启用"); return; }
        weChatNotices = new WeChatUnreadSource(
            notice => Dispatcher.BeginInvoke(() => { if (!Exiting) notifications?.Post(notice); }),
            key => Dispatcher.BeginInvoke(() => { if (!Exiting) notifications?.MarkRead(key); }),
            text => Dispatcher.BeginInvoke(() => { if (!Exiting) notifications?.SetWeChatStatus(text); }));
    }

    internal void WriteStatus() => File.WriteAllText(Path.Combine(Preferences.Root, "status.json"), JsonSerializer.Serialize(new
    {
        engineRunning = engine.Running, taskbars = taskbars?.Count ?? 0, taskbarSections = taskbars?.SectionCount ?? 0,
        roundedRegionsPresent = taskbars?.RoundedRegionsPresent() ?? false,
        leftClock = new { visible = taskbars?.ClockVisible ?? false, text = taskbars?.ClockText,
            left = taskbars?.ClockBounds.Left, top = taskbars?.ClockBounds.Top, width = taskbars?.ClockBounds.Width, height = taskbars?.ClockBounds.Height },
        startMenu = new { enabled = preferences.StyleStartMenu, running = startMenu.Running, loaded = startMenu.Loaded, searchLoaded = startMenu.SearchLoaded, status = startMenu.Status },
        taskbarScale = new { running = taskbarScale.Running, loaded = taskbarScale.Loaded, applied = taskbarScale.Applied, height = taskbarScale.Height, iconSize = taskbarScale.IconSize, buttonWidth = taskbarScale.ButtonWidth, status = taskbarScale.Status },
        trayGlass = new { status = trayGlass.Status },
        desktopRegions = desktop?.Count ?? 0, desktopBlurRegions = desktop?.BlurCount ?? 0, desktopFoldableRegions=desktop?.FoldableCount??0, desktopCollapsedRegions=desktop?.CollapsedCount??0, desktopBlur = preferences.Blur, desktopStatus = desktop?.Status,
        preferences.Opacity, preferences.Radius, preferences.Blur, preferences.TaskbarColor, preferences.NotificationColor, preferences.DesktopColor,
        startup = TaskGlass.Startup.Enabled, startupCommand = TaskGlass.Startup.Command,
        program = Environment.ProcessPath, updated = DateTimeOffset.Now,
        systemNotifications = notifications?.SystemStatus, clientNotifications = notifications?.ClientStatus,
        notificationCount = notifications?.Store.Items.Count ?? 0,
        lastPopupAt = notifications?.LastPopupAt,
        receivedByApp = notifications?.Received,
        weChatSummary = notifications?.WeChatStatus,
        weChatSummarySource = new { available = weChatNotices?.Available ?? false, cachedRows = weChatNotices?.CachedRows ?? 0 },
        weChatTaskbar = new { connected = weChatSignals?.Registered ?? false, signals = weChatSignals?.Signals ?? 0, posted = weChatSignals?.Posted ?? 0 }
        ,desktopModules = new { statusBars = statusBar?.Count ?? 0, launcherOpen = launcher?.IsOpen ?? false, applications = launcher?.AppCount ?? 0, controlCenterOpen = controlCenter?.IsVisible ?? false, hotkey = HotkeyStatus, error = moduleError },
        preferences.AnimationEnabled, preferences.ShowStatusBar, preferences.LauncherFullscreen, preferences.PerformancePreset
    }, new JsonSerializerOptions { WriteIndented = true }));

    internal void ShowSettings()
    {
        if (Exiting) return;
        settings ??= new SettingsWindow(this);
        settings.Show(); settings.Activate();
    }

    internal string NotificationStatus => notifications is null ? "正在连接消息来源…" : notifications.SystemStatus + "\n" + notifications.ClientStatus + "\n" + notifications.WeChatStatus + "\n" +
        (weChatSignals?.Registered == true ? "微信任务栏提醒已连接 · 摘要不可用时显示新消息提示" : "微信任务栏提醒未连接");
    internal void TestNotification() => notifications?.Test();
    internal string DesktopStatus => desktop?.Status ?? "桌面分区：正在连接…";
    internal int DesktopRegionCount => desktop?.Count ?? 0;
    internal int DesktopIconCount => desktop?.IconCount ?? 0;
    internal string StartMenuStatus => startMenu?.Status ?? "开始与搜索样式未开启";
    internal string HotkeyStatus => hotkey?.Status ?? "快捷键正在连接…";
    internal void ShowLauncher() { if (!Exiting && preferences.LauncherEnabled) { controlCenter?.Dismiss(); launcher?.Show(); WriteStatus(); } }
    internal void ShowControlCenter() => ShowControlCenterAt(DisplayGeometry.Current());
    private void ShowControlCenterAt(Forms.Screen screen)
    {
        if (Exiting || !preferences.ControlCenterEnabled) return;
        if (controlCenter is null)
        {
            controlCenter = new ControlCenterWindow(() => preferences, () => notifications?.SetPaused(!(notifications?.Paused ?? false)), () => notifications?.Paused ?? false);
            controlCenter.IsVisibleChanged += (_, _) => { if (!Exiting) WriteStatus(); };
        }
        launcher?.Hide();
        if (controlCenter.IsVisible && !controlCenter.IsClosing) controlCenter.Dismiss(); else controlCenter.ShowAt(screen);
        WriteStatus();
    }
    internal void ExportConfiguration(string path)
    { if (launcher is null) throw new InvalidOperationException("启动台尚未就绪。"); SettingsBackup.Save(path, SettingsBackup.Serialize(preferences, launcher.ExportData())); }
    internal void ImportConfiguration(string path)
    {
        if (launcher is null) throw new InvalidOperationException("启动台尚未就绪。");
        if (new FileInfo(path).Length > 4 * 1024 * 1024) throw new ArgumentException("配置文件超过 4 MB 限制。");
        var candidate = SettingsBackup.Parse(File.ReadAllText(path)); var previous = preferences; string previousLauncher = launcher.ExportData();
        try { launcher.ImportData(candidate.Launcher); Update(candidate.Preferences); }
        catch { launcher.ImportData(previousLauncher); Update(previous); throw; }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Exiting = true;
        desktop?.Dispose();
        hotkey?.Dispose(); statusBar?.Dispose(); controlCenter?.Close(); launcher?.Dispose();
        startMenu.Dispose();
        trayGlass.Dispose();
        taskbarScale.Dispose();
        showWait?.Unregister(null); exitWait?.Unregister(null); testWait?.Unregister(null);
        launcherWait?.Unregister(null); controlsWait?.Unregister(null); launcherEvent.Dispose(); controlsEvent.Dispose();
        windowsNotices?.Dispose(); clientNotices?.Dispose(); weChatNotices?.Dispose(); weChatSignals?.Dispose(); notifications?.Dispose();
        taskbars?.Dispose(); engine.Dispose();
        if (tray is not null) { tray.Visible = false; tray.Dispose(); }
        icon?.Dispose(); settings?.Close(); show.Dispose(); exit.Dispose(); test.Dispose();
        base.OnExit(e);
    }

    private static Icon CreateIcon()
    {
        using var stream = GetResourceStream(new Uri("/PathDeck.Desktop;component/Assets/TaskGlass.ico", UriKind.Relative))!.Stream;
        using var source = new Icon(stream, 32, 32);
        return (Icon)source.Clone();
    }
}
