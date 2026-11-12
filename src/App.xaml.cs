using System.ComponentModel;
using System.IO;
using System.Security;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

namespace QuickFileDesk;

public partial class App : Application
{
    private const string AppearanceFile = "appearance.json";
    private static readonly string WinESettingPath = Path.Combine(AppContext.BaseDirectory, "win-e.enabled");
    private MainWindow? browser;
    private WinEShortcut? shortcut;
    private Mutex? shortcutOwner;
    private DispatcherTimer? settingWatcher;

    internal bool DarkMode { get; private set; }
    internal bool WinEEnabled => File.Exists(WinESettingPath);
    internal string? WinEError { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            if (ReadAppearance().GetValueOrDefault("darkMode")) ApplyTheme(LoadPalette(darkMode: true), darkMode: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or JsonException)
        {
            // A missing or unreadable preference leaves the default light theme available.
        }
        ShowBrowser(e.Args.FirstOrDefault());
        if (!WinEEnabled) return;
        try { StartWinE(); }
        catch (Win32Exception ex) { WinEError = ex.Message; }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ReleaseWinE();
        base.OnExit(e);
    }

    internal void SetDarkMode(bool darkMode)
    {
        if (darkMode == DarkMode) return;
        var palette = LoadPalette(darkMode);
        PortableSettings.Update(AppearanceFile, @"Local\PathDeck.Appearance", ReadAppearance,
            settings => settings["darkMode"] = darkMode);
        ApplyTheme(palette, darkMode);
    }

    private static Dictionary<string, bool> ReadAppearance() =>
        PortableSettings.Load<Dictionary<string, bool>>(AppearanceFile) ?? [];

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

    internal void TrackWindowTheme(Window window)
    {
        window.SourceInitialized += (_, _) => WindowsIntegration.SetWindowFrameTheme(window, DarkMode);
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
            if (browser.WindowState == WindowState.Minimized) browser.WindowState = WindowState.Normal;
            browser.Activate();
            browser.ShowDriveOverview();
        }
    }

    private void ShowBrowser(string? folder)
    {
        var window = new MainWindow(folder);
        TrackWindowTheme(window);
        browser = window;
        MainWindow = window;
        window.Closed += (_, _) => browser = null;
        window.Show();
    }
}

