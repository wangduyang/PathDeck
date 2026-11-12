using System.ComponentModel;
using System.IO;
using System.Security;
using System.Windows;
using Microsoft.Win32;

namespace QuickFileDesk;

public partial class SettingsWindow : Window
{
    private readonly WindowsIntegration integration;

    public SettingsWindow() : this(new WindowsIntegration(Registry.CurrentUser, Environment.ProcessPath ?? "")) { }

    internal SettingsWindow(WindowsIntegration integration)
    {
        this.integration = integration;
        InitializeComponent();
    }

    private void ConfigureFileMenuClick(object sender, RoutedEventArgs e)
    {
        if (Owner is MainWindow browser) browser.ConfigureFileMenu(this);
    }

    private void ReadSettings(object sender, RoutedEventArgs e) => ChangeSettings(null);

    private void SelectTheme(object sender, RoutedEventArgs e)
    {
        if (Application.Current is App app)
            ChangeSettings(() => app.SetDarkMode(ReferenceEquals(sender, DarkTheme)));
    }

    private void ToggleWinE(object sender, RoutedEventArgs e)
    {
        if (Application.Current is App app)
            ChangeSettings(() => app.SetWinE(WinEToggle.IsChecked == true));
    }

    private void RefreshState()
    {
        var app = Application.Current as App;
        LightTheme.IsChecked = app?.DarkMode != true;
        DarkTheme.IsChecked = app?.DarkMode == true;
        var association = integration.ReadDefault();
        DefaultStatus.Text = association switch
        {
            IntegrationState.Enabled => "已为当前账户启用",
            IntegrationState.NeedsUpdate => "检测到部分关联或旧路径，可重新设置或恢复原设置",
            _ => "未启用"
        };
        EnableDefaultButton.IsEnabled = association != IntegrationState.Enabled;
        RestoreDefaultButton.IsEnabled = association != IntegrationState.Off;
        WinEToggle.IsEnabled = app is not null;
        WinEToggle.IsChecked = app?.WinEEnabled == true;
        WinEStatus.Text = app?.WinEError is { } error
            ? $"快捷键未启动（{error}）。请关闭后重新开启。"
            : "开启后，关闭窗口时 PathDeck 会留在后台响应 Win+E。";
        var startup = integration.ReadStartup();
        StartupToggle.IsChecked = startup != IntegrationState.Off;
        StartupStatus.Text = startup == IntegrationState.NeedsUpdate
            ? "启动项指向其他位置。关闭后重新开启即可更新。"
            : "登录 Windows 后打开 PathDeck。";
    }

    private void ChangeSettings(Action? change)
    {
        ErrorText.Visibility = Visibility.Collapsed;
        try { change?.Invoke(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or Win32Exception)
        {
            ShowError(ex.Message);
        }
        try { RefreshState(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or Win32Exception)
        {
            ShowError(ex.Message);
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = $"无法更新设置（{message}）";
        ErrorText.Visibility = Visibility.Visible;
    }

    private void EnableDefault(object sender, RoutedEventArgs e) => ChangeSettings(() => integration.SetDefault(true));
    private void RestoreDefault(object sender, RoutedEventArgs e) => ChangeSettings(() => integration.SetDefault(false));
    private void ToggleStartup(object sender, RoutedEventArgs e) => ChangeSettings(() => integration.SetStartup(StartupToggle.IsChecked == true));
}

