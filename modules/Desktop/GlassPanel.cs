using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace TaskGlass;

internal class GlassPanel : Window
{
    internal readonly Grid ContentHost = new();
    internal readonly StackPanel HeaderActions = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border surface;
    private Brush fallback = Brushes.Transparent;
    private readonly BackdropImage sample = new() { Stretch = Stretch.Fill, IsHitTestVisible = false };
    private BitmapSource? backdropSource;
    private readonly BlurEffect blur = new() { RenderingBias = RenderingBias.Performance };
    private readonly GlassTintEffect tintEffect = new();
    private readonly ScaleTransform scale = new(1, 1);
    private readonly TranslateTransform shift = new();
    private readonly DispatcherTimer closeTimer = new() { Interval = TimeSpan.FromMilliseconds(145) };
    private Preferences appearance = new();
    private bool dismissOutside, closing;
    internal bool IsClosing => closing;
    private readonly Grid motionShell;
    internal Point AnimationOrigin { get => motionShell.RenderTransformOrigin; set => motionShell.RenderTransformOrigin = value; }
    private readonly RadialGradientBrush glint = new(Color.FromArgb(190,220,250,238), Color.FromArgb(48,160,190,180)) { RadiusX=.6, RadiusY=.9, Center=new Point(.5,0), GradientOrigin=new Point(.5,0) };

    internal GlassPanel(string title, double width, double height)
    {
        Title = title; Width = width; Height = height; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true; Background = Brushes.Transparent; ShowInTaskbar = false; Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        FontFamily = new FontFamily("Segoe UI Variable Text, Microsoft YaHei UI"); FontSize = 14;
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/PathDeck.Desktop;component/SettingsStyles.xaml", UriKind.Relative) });
        var transform = new TransformGroup(); transform.Children.Add(scale); transform.Children.Add(shift);
        var shell = new Grid { Margin = new Thickness(12), RenderTransform = transform, RenderTransformOrigin = new Point(.5, .35) };
        motionShell = shell;
        Content = shell;
        var shadow = new Border { CornerRadius = new CornerRadius(26), Background = Brushes.Black,
            Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 6, Opacity = .28 } };
        shell.Children.Add(shadow);
        var layers = new Grid(); shell.Children.Add(layers); sample.Effect = blur;
        layers.Children.Add(new Border { Child = sample, Effect = tintEffect, IsHitTestVisible = false });
        surface = new Border { CornerRadius = new CornerRadius(26), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromArgb(84, 220, 235, 230)) };
        layers.Children.Add(surface);
        surface.BorderBrush = glint;
        MouseMove += (_, e) => { if (!GlassMotion.Active || surface.ActualWidth <= 0 || surface.ActualHeight <= 0) return; var point=e.GetPosition(surface);var relative=new Point(point.X/surface.ActualWidth,point.Y/surface.ActualHeight);if(Math.Abs(relative.X-glint.Center.X)+Math.Abs(relative.Y-glint.Center.Y)>.015){glint.Center=relative;glint.GradientOrigin=relative;} };
        var layout = new Grid { Margin = new Thickness(24, 12, 24, 24) };
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(52) }); layout.RowDefinitions.Add(new RowDefinition());
        surface.Child = layout;
        var header = new DockPanel { LastChildFill = true };
        var close = new Button { Content = "×", Width = 44, Height = 44, FontSize = 23, Padding = new Thickness(0), Style = (Style)FindResource("SettingsQuietButton"), ToolTip = "关闭" };
        close.Click += (_, _) => Dismiss(); DockPanel.SetDock(close, Dock.Right); header.Children.Add(close);
        DockPanel.SetDock(HeaderActions, Dock.Right); header.Children.Add(HeaderActions);
        var titleText = new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        header.Children.Add(titleText); layout.Children.Add(header);
        header.MouseLeftButtonDown += (_, e) => { if (e.OriginalSource == titleText && e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        Grid.SetRow(ContentHost, 1); layout.Children.Add(ContentHost);
        layers.SizeChanged += (_, _) => layers.Clip = new RectangleGeometry(new Rect(0, 0, layers.ActualWidth, layers.ActualHeight), 26, 26);
        closeTimer.Tick += (_, _) => { closeTimer.Stop(); if (closing) { Hide(); closing = false; } };
        sample.SnapshotChanged += () =>
        {
            backdropSource = sample.Source as BitmapSource;
            surface.Background = backdropSource is null ? fallback : Brushes.Transparent;
            sample.Visibility = backdropSource is null ? Visibility.Hidden : Visibility.Visible;
        };
        sample.Attach(this);
        Deactivated += (_, _) => { if (dismissOutside && IsVisible && OwnedWindows.Count == 0) Dismiss(); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Dismiss(); } };
        Closed += (_, _) => closeTimer.Stop();
        ApplyAppearance(appearance);
    }
    internal void SetBackdrop(BitmapSource? value)
    {
        backdropSource = value; sample.Source = value;
        surface.Background = value is null ? fallback : Brushes.Transparent;
        sample.Visibility = value is null ? Visibility.Hidden : Visibility.Visible;
    }
    private void SetTint()
    {
        var palette = GlassTheme.Material(appearance.TaskbarColor, appearance.Opacity);
        fallback = palette.Fallback; SetBackdrop(backdropSource); tintEffect.Apply(palette);
        Foreground = new SolidColorBrush(palette.Foreground);
        Resources["SettingsTextBrush"] = Foreground; Resources["SettingsMutedBrush"] = Foreground;
        Resources["SettingsSurfaceBrush"] = GlassTheme.Surface(palette);
        Resources["SettingsAccent"] = new SolidColorBrush(palette.Foreground == Colors.White ? Color.FromRgb(136, 226, 196) : Color.FromRgb(29, 88, 66));
        Resources["SettingsAccentText"] = new SolidColorBrush(palette.Foreground == Colors.White ? Color.FromRgb(23,59,48) : Colors.White);
        var sheet = (Color)ColorConverter.ConvertFromString(appearance.TaskbarColor); sheet.A = 250;
        Resources["SettingsSheetBrush"] = new SolidColorBrush(sheet);
        sample.Visibility = sample.Source is not null ? Visibility.Visible : Visibility.Hidden;
    }
    internal void ApplyAppearance(Preferences value)
    {
        bool resume = appearance.Blur == 0 && value.Blur > 0; appearance = value;
        blur.Radius = value.Blur * .6;
        SetTint(); if (resume) sample.Refresh();
        if (!GlassMotion.Active && closing) { closeTimer.Stop(); Hide(); closing = false; }
        if (!GlassMotion.Active) { ContentHost.BeginAnimation(OpacityProperty,null);ContentHost.Opacity=1; }
    }
    internal void Present(bool dismissOnDeactivate = true)
    {
        dismissOutside = dismissOnDeactivate; closeTimer.Stop(); closing = false; IsHitTestVisible = true;
        bool fresh = !IsVisible;
        if (fresh) { Opacity = GlassMotion.Active ? 0 : 1; scale.ScaleX = scale.ScaleY = GlassMotion.Active ? .96 : 1; shift.Y = GlassMotion.Active ? 8 : 0; Show(); }
        Activate(); sample.Refresh();
        GlassMotion.To(this, OpacityProperty, 1, 180); GlassMotion.To(scale, ScaleTransform.ScaleXProperty, 1, 180);
        GlassMotion.To(scale, ScaleTransform.ScaleYProperty, 1, 180); GlassMotion.To(shift, TranslateTransform.YProperty, 0, 180);
        if(fresh && GlassMotion.Active)
        {
            var fade=new DoubleAnimationUsingKeyFrames {FillBehavior=FillBehavior.Stop};
            fade.KeyFrames.Add(new DiscreteDoubleKeyFrame(0,KeyTime.FromTimeSpan(TimeSpan.Zero)));
            fade.KeyFrames.Add(new DiscreteDoubleKeyFrame(0,KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(35))));
            fade.KeyFrames.Add(new EasingDoubleKeyFrame(1,KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(155))) {EasingFunction=new CubicEase {EasingMode=EasingMode.EaseOut}});
            ContentHost.BeginAnimation(OpacityProperty,fade);
        }
    }
    internal void Dismiss()
    {
        if (!IsVisible || closing) return;
        closing = true; IsHitTestVisible = false;
        if (!GlassMotion.Active) { Hide(); closing = false; return; }
        GlassMotion.To(this, OpacityProperty, 0, 140); GlassMotion.To(scale, ScaleTransform.ScaleXProperty, .98, 140);
        GlassMotion.To(scale, ScaleTransform.ScaleYProperty, .98, 140); closeTimer.Start();
    }
}
