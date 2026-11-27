using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TaskGlass;

public partial class SettingsWindow : Window
{
    private readonly GlassApplication app;
    private bool ready;
    private BitmapSource? backdropSource;
    private readonly GlassTintEffect tintEffect = new();
    internal SettingsWindow(GlassApplication app)
    {
        this.app = app; InitializeComponent();
        BuildVersionText.Text = "PathDeck " + System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(SettingsWindow).Assembly)!.InformationalVersion;
        SettingsMaterialLayer.Effect = tintEffect;
        MaxHeight = SystemParameters.WorkArea.Height;
        Height = Math.Min(760, Math.Max(1, SystemParameters.WorkArea.Height - 24));
        Width = Math.Min(960, Math.Max(1, SystemParameters.WorkArea.Width - 24));
        OpacitySlider.Value = app.Preferences.Opacity; RadiusSlider.Value = app.Preferences.Radius;
        BlurSlider.Value = app.Preferences.Blur;
        GlassTint.Background = GlassTint.Background.Clone();
        ReadColors();
        SizeChanged += (_, _) => BackdropLayers.Clip = new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight), 30, 30);
        SettingsBackdrop.SnapshotChanged += () =>
        {
            backdropSource = SettingsBackdrop.Source as BitmapSource;
            SettingsBackdrop.Visibility = backdropSource is null ? Visibility.Collapsed : Visibility.Visible;
            GlassTint.Background = backdropSource is null ? GlassTheme.Material(app.Preferences.TaskbarColor, app.Preferences.Opacity).Fallback : Brushes.Transparent;
        };
        SettingsBackdrop.Attach(this);
        SetBlur(app.Preferences.Blur);
        WeChatSummaryCheck.IsChecked = app.Preferences.ReadWeChatUnread;
        ReadStartup(); ReadNotificationStatus(); ReadDesktopStatus(); ReadStartMenuStatus(); ReadRuntimeState(); BuildModules(); ready = true;
    }
    internal void ReadColors()
    {
        var value = app.Preferences;
        TaskbarColorText.Text = value.TaskbarColor;
        TaskbarSwatch.Background = (Brush)new BrushConverter().ConvertFromString(value.TaskbarColor)!;
        ApplyMaterial();
    }
    private void ApplyMaterial()
    {
        var material = GlassTheme.Material(app.Preferences.TaskbarColor, app.Preferences.Opacity);
        SettingsBackdrop.Source = backdropSource; tintEffect.Apply(material);
        SettingsBackdrop.Visibility = backdropSource is null ? Visibility.Collapsed : Visibility.Visible;
        GlassTint.Background = backdropSource is null ? material.Fallback : Brushes.Transparent;
        Foreground = new SolidColorBrush(material.Foreground);
        Resources["SettingsTextBrush"] = Foreground; Resources["SettingsMutedBrush"] = Foreground;
        Resources["SettingsSurfaceBrush"] = GlassTheme.Surface(material);
        Resources["SettingsAccent"] = new SolidColorBrush(material.Foreground == Colors.White ? Color.FromRgb(136,226,196) : Color.FromRgb(29,88,66));
        Resources["SettingsAccentText"] = new SolidColorBrush(material.Foreground == Colors.White ? Color.FromRgb(23,59,48) : Colors.White);
        var sheet = material.Tint; sheet.A = 250;
        Resources["SettingsSheetBrush"] = new SolidColorBrush(sheet);
    }
    private void ChooseColor(object sender, RoutedEventArgs e)
    {
        if (sender is not Button) return;
        try
        {
            var value = app.Preferences;
            var picker = new ColorPickerWindow(value.TaskbarColor, "文件管理、任务栏、通知与桌面分区", value) { Owner = this };
            if (picker.ShowDialog() != true) return;
            string color = picker.SelectedColor;
            value = value with { TaskbarColor = color };
            app.Update(value); StatusText.Text = "";
        }
        catch (Exception error) { StatusText.Text = error.Message; }
    }
    internal void SetBlur(double value)
    {
        SettingsBlurEffect.Radius = value * .6;
        ApplyMaterial();
        if (backdropSource is null) SettingsBackdrop.Refresh();
    }
    internal void ReadStartup() => StartupCheck.IsChecked = Startup.Enabled;
    internal void ReadStartMenuStatus()
    {
        StartMenuCheck.IsChecked = app.Preferences.StyleStartMenu;
        StartMenuStatusText.Text = app.StartMenuStatus;
    }
    private void SetStartMenu(object sender, RoutedEventArgs e)
    {
        if (!ready || app.Preferences.StyleStartMenu == (StartMenuCheck.IsChecked == true)) return;
        try { app.Update(app.Preferences with { StyleStartMenu = StartMenuCheck.IsChecked == true }); StatusText.Text = ""; }
        catch (Exception error) { StatusText.Text = error.Message; }
        ReadStartMenuStatus();
    }
    internal void ReadNotificationStatus()
    {
        NotificationStatusText.Text = app.NotificationStatus;
        NotificationSummaryText.Text = app.NotificationStatus.Split('\n')[0];
        ReadRuntimeState();
    }
    internal void ReadDesktopStatus()
    {
        DesktopStatusText.Text = app.DesktopStatus;
        DesktopStatusText.Visibility = app.DesktopRegionCount > 0 ? Visibility.Collapsed : Visibility.Visible;
        DesktopCountText.Text = $"{app.DesktopRegionCount} 个区域";
        DesktopCountText.ToolTip = app.DesktopStatus;
        DesktopIconCountText.Text = $"{app.DesktopIconCount} 个图标";
        ReadRuntimeState();
    }
    private void ReadRuntimeState() => RuntimeStatusText.Text = app.State;
    private void TestNotification(object sender, RoutedEventArgs e)
    {
        try { app.TestNotification(); StatusText.Text = ""; }
        catch (Exception error) { StatusText.Text = error.Message; }
    }
    private void SetWeChatSummary(object sender, RoutedEventArgs e)
    {
        try { app.Update(app.Preferences with { ReadWeChatUnread = WeChatSummaryCheck.IsChecked == true }); StatusText.Text = ""; ReadRuntimeState(); }
        catch (Exception error) { StatusText.Text = error.Message; WeChatSummaryCheck.IsChecked = app.Preferences.ReadWeChatUnread; }
    }
    private void ChangeAppearance(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!ready) return;
        try { app.Update(app.Preferences with { Opacity = OpacitySlider.Value, Radius = RadiusSlider.Value, Blur = BlurSlider.Value }); StatusText.Text = ""; ReadRuntimeState(); }
        catch (Exception error) { StatusText.Text = error.Message; }
    }
    private void SetStartup(object sender, RoutedEventArgs e)
    {
        try { Startup.Enabled = StartupCheck.IsChecked == true; app.WriteStatus(); StatusText.Text = ""; }
        catch (Exception error) { StatusText.Text = error.Message; ReadStartup(); }
    }
    private void HidePanel(object? sender, CancelEventArgs e) { if (!app.Exiting) { e.Cancel = true; Hide(); } }
    private void ClosePanel(object sender, RoutedEventArgs e) => Hide();
    private void RestoreAndExit(object sender, RoutedEventArgs e) => app.Shutdown();
}
