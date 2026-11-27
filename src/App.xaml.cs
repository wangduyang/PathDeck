using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace QuickFileDesk;

public partial class App : Application
{
    private static readonly string WinESettingPath = Path.Combine(AppContext.BaseDirectory, "win-e.enabled");
    private MainWindow? browser;
    private WindowGlass? browserGlass;
    private WinEShortcut? shortcut;
    private Mutex? shortcutOwner;
    private DispatcherTimer? settingWatcher;
    private SharedAppearance? appearance;
    private EventWaitHandle? showFiles, showFileSettings;
    private RegisteredWaitHandle? filesWait, fileSettingsWait;
    private Mutex? navigationOwner;

    internal bool DarkMode { get; private set; }
    internal bool WinEEnabled => File.Exists(WinESettingPath);
    internal string? WinEError { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        TaskGlass.GlassMenus.Install(this);
        appearance = new SharedAppearance(this);
        DesktopModule.EnsureStarted();
        bool background = e.Args.Contains("--background");
        if (!background) ShowBrowser(e.Args.FirstOrDefault(arg => !arg.StartsWith("--")));
        StartModuleNavigation();
        if (e.Args.Contains("--file-settings")) ShowFileSettings();
        if (!WinEEnabled) { if (background) Shutdown(); return; }
        try { StartWinE(); }
        catch (Win32Exception ex) { WinEError = ex.Message; }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ReleaseWinE();
        appearance?.Dispose();
        filesWait?.Unregister(null); fileSettingsWait?.Unregister(null);
        showFiles?.Dispose(); showFileSettings?.Dispose();
        navigationOwner?.ReleaseMutex(); navigationOwner?.Dispose();
        base.OnExit(e);
    }

    private static ResourceDictionary LoadPalette(bool darkMode) => new()
    {
        Source = new Uri($"/{typeof(App).Assembly.GetName().Name};component/Theme{(darkMode ? "Dark" : "Light")}.xaml", UriKind.Relative)
    };

    private void ApplyTheme(ResourceDictionary palette, bool darkMode)
    {
        DarkMode = darkMode;
        Resources.MergedDictionaries[0] = palette;
        foreach (Window window in Windows) WindowsIntegration.SetWindowFrameTheme(window, darkMode);
    }

    internal void ApplySharedPalette(bool darkMode) => ApplyTheme(LoadPalette(darkMode), darkMode);

    private void StartModuleNavigation()
    {
        var owner = new Mutex(true, @"Local\PathDeck.Files.Navigation", out bool first);
        if (!first) { owner.Dispose(); return; }
        navigationOwner = owner;
        showFiles = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\PathDeck.Files.Show");
        showFileSettings = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\PathDeck.Files.Settings");
        filesWait = ThreadPool.RegisterWaitForSingleObject(showFiles, (_, _) => Dispatcher.BeginInvoke(ActivateBrowser), null, -1, false);
        fileSettingsWait = ThreadPool.RegisterWaitForSingleObject(showFileSettings, (_, _) => Dispatcher.BeginInvoke(ShowFileSettings), null, -1, false);
    }

    private void ActivateBrowser()
    {
        bool created=browser is null;
        if (created) ShowBrowser(BrowserTab.DriveOverviewPath);
        if (browser!.WindowState == WindowState.Minimized)
        {
            browserGlass?.Prepare();
            browser.WindowState = WindowState.Normal;
        }
        else if(!created) browserGlass?.Refresh();
        browser.Activate();
    }

    internal void ShowFileSettings()
    {
        ActivateBrowser();
        var existing = Windows.OfType<SettingsWindow>().FirstOrDefault();
        if (existing is not null) { existing.Activate(); return; }
        var settings = new SettingsWindow { Owner = browser, Icon = browser?.Icon };
        TrackWindowTheme(settings); settings.Show();
    }

    internal WindowGlass? TrackWindowTheme(Window window)
    {
        var glass = appearance is not null ? new WindowGlass(window, appearance) : null;
        window.SourceInitialized += (_, _) => WindowsIntegration.SetWindowFrameTheme(window, DarkMode);
        return glass;
    }

    internal void SetWinE(bool enabled)
    {
        if (!enabled)
        {
            File.Delete(WinESettingPath);
            WinEError = null;
            StopWinE();
            return;
        }

        var wasEnabled = WinEEnabled;
        if (!wasEnabled) File.WriteAllText(WinESettingPath, "");
        try { StartWinE(); }
        catch (Win32Exception ex)
        {
            if (!wasEnabled) File.Delete(WinESettingPath);
            WinEError = ex.Message;
            throw;
        }
    }

    private void StartWinE()
    {
        WinEError = null;
        if (shortcut is not null) return;
        var mutex = new Mutex(false, @"Local\1Commander.WinE");
        var owns = false;
        try
        {
            try { owns = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { owns = true; }
            if (!owns) return;

            shortcut = new WinEShortcut(() => Dispatcher.BeginInvoke(DispatcherPriority.Normal, OpenFromShortcut));
            shortcutOwner = mutex;
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            settingWatcher = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            settingWatcher.Tick += (_, _) => { if (!WinEEnabled) StopWinE(); };
            settingWatcher.Start();
        }
        finally
        {
            if (shortcutOwner != mutex)
            {
                if (owns) mutex.ReleaseMutex();
                mutex.Dispose();
            }
        }
    }

    private void StopWinE()
    {
        if (shortcut is null) return;
        ReleaseWinE();
        ShutdownMode = ShutdownMode.OnLastWindowClose;
        if (Windows.Count == 0) Shutdown();
    }

    private void ReleaseWinE()
    {
        settingWatcher?.Stop();
        settingWatcher = null;
        shortcut?.Dispose();
        shortcut = null;
        shortcutOwner?.ReleaseMutex();
        shortcutOwner?.Dispose();
        shortcutOwner = null;
    }

    private void OpenFromShortcut()
    {
        if (shortcut is null) return;
        if (browser is null) ShowBrowser(BrowserTab.DriveOverviewPath);
        else
        {
            if (browser.WindowState == WindowState.Minimized)
            {
                browserGlass?.Prepare();
                browser.WindowState = WindowState.Normal;
            }
            else browserGlass?.Refresh();
            browser.Activate();
            browser.ShowDriveOverview();
        }
    }

    private void ShowBrowser(string? folder)
    {
        var window = new MainWindow(folder);
        browserGlass=TrackWindowTheme(window);
        browser = window;
        MainWindow = window;
        window.Closed += (_, _) => {browser = null;browserGlass=null;};
        browserGlass?.Prepare();
        window.Show();
    }
}

