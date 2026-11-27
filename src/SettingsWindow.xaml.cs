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
        BuildVersionText.Text = typeof(SettingsWindow).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().Single().InformationalVersion;
    }

    private void OpenHelp(object sender, RoutedEventArgs e)
    {
        var help = new HelpWindow { Owner = this };
        (Application.Current as App)?.TrackWindowTheme(help);
        help.ShowDialog();
    }

    private void ConfigureFileMenuClick(object sender, RoutedEventArgs e)
    {
        if (Owner is MainWindow browser) browser.ConfigureFileMenu(this);
    }

    private void ReadSettings(object sender, RoutedEventArgs e) => ChangeSettings(null);
    private void CloseSettings(object sender, RoutedEventArgs e) => Close();

    private void ToggleWinE(object sender, RoutedEventArgs e)
    {
        if (Application.Current is App app)
            ChangeSettings(() => app.SetWinE(WinEToggle.IsChecked == true));
    }

    private void RefreshState()
    {
        var app = Application.Current as App;
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
}

