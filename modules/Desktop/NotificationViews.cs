using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;

namespace TaskGlass;

internal class NoticeWindow : Window
{
    internal readonly StackPanel Body = new();
    private static readonly Dictionary<string, ImageSource> localIcons = new(StringComparer.Ordinal);
    private nint handle;
    private readonly ImageBrush backdrop = new() { Stretch = Stretch.Fill };
    private BitmapSource? backdropSource;
    private double materialOpacity = 45;
    private readonly BlurEffect backdropBlur = new() { Radius = 36, RenderingBias = RenderingBias.Performance };
    private readonly GlassTintEffect tintEffect = new();
    private readonly Border backdropLayer;
    private readonly Border glass;
    private string color = GlassTheme.NotificationTint;
    internal double Blur { get; private set; } = 60;
    internal double StackOffset;
    private double previousY = double.NaN;
    internal NoticeWindow()
    {
        Title = "PathDeck 通知"; Width = 320; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        ShowInTaskbar = false; ShowActivated = false; Topmost = true; SizeToContent = SizeToContent.Height;
        FontFamily = new FontFamily("Segoe UI Variable Text, Microsoft YaHei UI"); FontSize = 13.5;
        Foreground = new SolidColorBrush(Color.FromRgb(32, 40, 39)); Background = Brushes.Transparent;
        Resources["NoticeBodyBrush"] = new SolidColorBrush(Color.FromRgb(48, 60, 58));
        Resources["NoticeTimeBrush"] = new SolidColorBrush(Color.FromRgb(69, 87, 75));
        SetCloseColors(null);
        WindowStartupLocation = WindowStartupLocation.Manual; MaxHeight = SystemParameters.WorkArea.Height - 32;
        var edge = GlassTheme.Rim();
        var tint = new LinearGradientBrush(Color.FromArgb(182, 247, 249, 249), Color.FromArgb(164, 235, 240, 240), 85);
        glass = new Border { CornerRadius = new CornerRadius(28), Background = tint, BorderBrush = edge,
            BorderThickness = new Thickness(1), Padding = new Thickness(18, 16, 18, 16), Child = Body };
        var light = new RadialGradientBrush(GlassTheme.Highlight, Colors.Transparent)
        { Center = new System.Windows.Point(0.2, 0), GradientOrigin = new System.Windows.Point(0.2, 0), RadiusX = .7, RadiusY = 1.3 };
        var glint = new Border { CornerRadius = new CornerRadius(28), BorderThickness = new Thickness(1.5), BorderBrush = light,
            IsHitTestVisible = false, Opacity = .24 };
        backdropLayer = new Border { Background = backdrop, Effect = backdropBlur, IsHitTestVisible = false };
        var layers = new Grid(); layers.Children.Add(new Border { Child = backdropLayer, Effect = tintEffect, IsHitTestVisible = false }); layers.Children.Add(glass); layers.Children.Add(glint); Content = layers;
        layers.SizeChanged += (_, _) => layers.Clip = new RectangleGeometry(new Rect(0, 0, layers.ActualWidth, layers.ActualHeight), 28, 28);
        MouseMove += (_, e) =>
        {
            if (!GlassMotion.Active) return;
            var point = e.GetPosition(this);
            light.Center = light.GradientOrigin = new System.Windows.Point(point.X / Math.Max(1, ActualWidth), point.Y / Math.Max(1, ActualHeight));
        };
        void SetGlint(double value)
        {
            if (GlassMotion.Active) glint.BeginAnimation(OpacityProperty, new DoubleAnimation(value, TimeSpan.FromMilliseconds(130)));
            else { glint.BeginAnimation(OpacityProperty, null); glint.Opacity = value; }
        }
        MouseEnter += (_, _) => SetGlint(.46);
        MouseLeave += (_, _) => SetGlint(.24);
        SourceInitialized += (_, _) =>
        {
            handle = new WindowInteropHelper(this).Handle;
            if (HwndSource.FromHwnd(handle)?.CompositionTarget is { } target) target.BackgroundColor = Colors.Transparent;
            Native.GlassFrame(handle, true);
        };
        Loaded += (_, _) => Place(); SizeChanged += (_, _) => { if (IsVisible) Place(); };
        Closed += (_, _) => { backdropSource = null; backdrop.ImageSource = null; };
        ApplyMaterial();
    }
    internal void Place()
    {
        if (handle == 0 || ActualHeight <= 0) return;
        var work = Forms.Screen.PrimaryScreen!.WorkingArea;
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        int width = (int)Math.Round(ActualWidth * scale), height = (int)Math.Round(ActualHeight * scale);
        int margin = (int)Math.Round(16 * scale);
        int x = work.Right - width - margin;
        int y = Math.Max(work.Top + margin, work.Bottom - height - margin - (int)Math.Round(StackOffset * scale));
        double targetY = y / scale, fromY = Top;
        bool animate = GlassMotion.Active && double.IsFinite(previousY) && Math.Abs(previousY - targetY) > .5;
        BeginAnimation(TopProperty, null);
        Left = x / scale; Top = targetY;
        Native.SetWindowPos(handle, -1, x, y, 0, 0, 0x0001 | 0x0010);
        if (animate) BeginAnimation(TopProperty, new DoubleAnimation(fromY, targetY, TimeSpan.FromMilliseconds(180)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop });
        previousY = targetY;
    }
    internal void SetBackdrop(BitmapSource image) { backdropSource = image; ApplyMaterial(); }
    private void SetCloseColors(Color? text)
    {
        byte shade = text == Colors.White ? (byte)0 : (byte)255;
        Resources["NoticeCloseTextBrush"] = new SolidColorBrush(text ?? Color.FromRgb(43,57,53));
        Resources["NoticeClosePressedTextBrush"] = new SolidColorBrush(text ?? Colors.White);
        Resources["NoticeCloseFocusBrush"] = new SolidColorBrush(text ?? Color.FromRgb(55,89,75));
        Resources["NoticeCloseBackground"] = new SolidColorBrush(text.HasValue ? Color.FromArgb(32,shade,shade,shade) : Color.FromArgb(48,255,255,255));
        Resources["NoticeCloseHoverBackground"] = new SolidColorBrush(text.HasValue ? Color.FromArgb(69,shade,shade,shade) : Color.FromArgb(112,255,255,255));
        Resources["NoticeClosePressedBackground"] = new SolidColorBrush(text.HasValue ? Color.FromArgb(112,shade,shade,shade) : Color.FromArgb(144,0,0,0));
    }
    internal void SetColor(string value)
    {
        if (color == value) return;
        color = value;
        ApplyMaterial();
    }
    internal void SetMaterialOpacity(double value) { if (materialOpacity == value) return; materialOpacity = value; ApplyMaterial(); }
    private void ApplyMaterial()
    {
        var material = GlassTheme.Material(color, materialOpacity);
        backdrop.ImageSource = backdropSource; tintEffect.Apply(material);
        glass.Background = backdropSource is null ? material.Fallback : Brushes.Transparent;
        Foreground = new SolidColorBrush(material.Foreground);
        Resources["NoticeBodyBrush"] = Foreground; Resources["NoticeTimeBrush"] = Foreground;
        SetCloseColors(material.Foreground);
    }
    internal void SetBlur(double value)
    {
        Blur = double.IsNaN(value) ? 60 : Math.Clamp(value, 0, 100);
        backdropBlur.Radius = Blur * .6;
        backdropLayer.Visibility = Visibility.Visible;
    }
    internal static ImageSource? WeChatIcon() => AppIcon("微信");
    internal static ImageSource? AppIcon(string app)
    {
        if (localIcons.TryGetValue(app, out var cached)) return cached;
        string[] processes = app switch { "微信" => new[] { "Weixin", "WeChat" }, "阿里邮箱" => new[] { "AliMail" }, _ => Array.Empty<string>() };
        foreach (string name in processes)
            foreach (var process in Process.GetProcessesByName(name))
                using (process)
                    try
                    {
                        if (process.MainModule?.FileName is not string path) continue;
                        using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
                        if (icon is null) continue;
                        var bitmap = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(32, 32));
                        bitmap.Freeze(); localIcons[app] = bitmap; return bitmap;
                    }
                    catch (Exception) { }
        return null;
    }
}

internal sealed class ToastCard : NoticeWindow
{
    internal Notice Notice { get; }
    internal ToastCard(NotificationHub hub, Notice notice)
    {
        Notice = notice; Cursor = Cursors.Hand; Body.Focusable = true;
        KeyboardNavigation.SetIsTabStop(Body, true);
        Body.FocusVisualStyle = (Style)Application.Current.FindResource("NoticeCardFocus");
        System.Windows.Automation.AutomationProperties.SetName(Body, notice.App + "新提醒，点击打开");
        var layout = new Grid(); layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });
        layout.ColumnDefinitions.Add(new ColumnDefinition());
        var close = new Button { Content = "×", Width = 44, Height = 44, FontSize = 21, Padding = new Thickness(0), Margin = new Thickness(0, -10, -8, 0),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Opacity = 0,
            Style = (Style)Application.Current.FindResource("NoticeButton"), ToolTip = "关闭这条提醒" };
        System.Windows.Automation.AutomationProperties.SetName(close, "关闭这条提醒");
        close.Click += (_, e) => { e.Handled = true; hub.Remove(notice); };
        var time = new TextBlock { Text = notice.TimeText, FontSize = 11.5, FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Right };
        time.SetResourceReference(TextBlock.ForegroundProperty,"NoticeTimeBrush");
        var icon = notice.Icon ?? AppIcon(notice.App);
        var badge = new Border { Width = 44, Height = 44, CornerRadius = new CornerRadius(13), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
            Background = icon is null ? (Brush)new BrushConverter().ConvertFromString(notice.Accent)! : Brushes.Transparent };
        badge.Child = icon is not null ? new Image { Source = icon, Stretch = Stretch.Uniform } :
            new TextBlock { Text = notice.Symbol, FontSize = 14, FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(24, 42, 34)), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        layout.Children.Add(badge);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = notice.Title, FontSize = 14, FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, LineHeight = 20, MaxHeight = 40, Margin = new Thickness(0, 0, 52, 0) });
        if (notice.Body.Length > 0)
        {
            var message = new TextBlock { Text = notice.Body, TextWrapping = TextWrapping.Wrap, LineHeight = 20,
                TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 40, Margin = new Thickness(0, 4, 44, 0) };
            message.SetResourceReference(TextBlock.ForegroundProperty,"NoticeBodyBrush"); text.Children.Add(message);
        }
        Grid.SetColumn(text, 1); layout.Children.Add(text); Grid.SetColumn(time, 1); layout.Children.Add(time);
        Grid.SetColumn(close, 1); layout.Children.Add(close); Body.Children.Add(layout);
        void RefreshClose()
        {
            bool show = IsMouseOver || close.IsKeyboardFocusWithin;
            close.Opacity = show ? 1 : 0; time.Opacity = show ? 0 : 1;
        }
        MouseEnter += (_, _) => RefreshClose();
        MouseLeave += (_, _) => RefreshClose();
        close.IsKeyboardFocusWithinChanged += (_, _) => RefreshClose();
        MouseLeftButtonUp += (_, e) => { e.Handled = true; hub.Open(notice); };
        KeyDown += (_, e) => { if (!close.IsKeyboardFocusWithin && (e.Key is Key.Enter or Key.Space)) { e.Handled = true; hub.Open(notice); } };
        Loaded += (_, _) =>
        {
            if (!GlassMotion.Active) return;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            Body.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)) { EasingFunction = ease });
            var shift = new TranslateTransform(); Body.RenderTransform = shift;
            shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(12, 0, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
        };
    }
}

internal sealed class ToastStack : IDisposable
{
    private readonly NotificationHub hub;
    private readonly List<ToastCard> cards = new();
    private bool arranging;
    private double blur = 60;
    private double materialOpacity = 45;
    private string color = GlassTheme.NotificationTint;
    internal void SetColor(string value)
    {
        color = value;
        foreach (var card in cards) card.SetColor(value);
    }
    internal ToastStack(NotificationHub hub) => this.hub = hub;
    internal void SetMaterialOpacity(double value) { materialOpacity = value; foreach (var card in cards) card.SetMaterialOpacity(value); }
    internal void SetBlur(double value)
    {
        double previous = blur;
        blur = double.IsNaN(value) ? 60 : Math.Clamp(value, 0, 100);
        foreach (var card in cards) card.SetBlur(blur);
        if (previous == 0 && blur > 0) GlassSnapshot.Refresh(cards.Where(card => card.IsVisible && card.ActualHeight > 0).ToArray());
    }
    internal void Refresh()
    {
        foreach (var card in cards.Where(card => !hub.Store.Items.Contains(card.Notice)).ToArray())
        { cards.Remove(card); card.Close(); }
        var visible = new HashSet<ToastCard>();
        double used = 0, available = SystemParameters.WorkArea.Height - 32;
        // Oldest unhandled notices keep their place; newer ones wait when the screen is full.
        for (int i = hub.Store.Items.Count - 1; i >= 0; i--)
        {
            var notice = hub.Store.Items[i];
            var card = cards.FirstOrDefault(item => ReferenceEquals(item.Notice, notice));
            bool fresh = card is null;
            card ??= new ToastCard(hub, notice);
            var content = (FrameworkElement)card.Content;
            content.Measure(new Size(400, available));
            double height = card.IsVisible ? card.ActualHeight : Math.Ceiling(content.DesiredSize.Height);
            double gap = visible.Count == 0 ? 0 : 12;
            if (visible.Count > 0 && used + gap + height > available)
            { if (fresh) card.Close(); break; }
            visible.Add(card); used += gap + height;
            if (fresh)
            {
                card.SetColor(color);
                card.SetMaterialOpacity(materialOpacity);
                card.SetBlur(blur);
                cards.Add(card); card.SizeChanged += (_, _) => Arrange();
                card.Show();
            }
        }
        foreach (var card in cards.Where(card => !visible.Contains(card)).ToArray())
        { cards.Remove(card); card.Close(); }
        Arrange();
    }
    internal void Close(string key)
    {
        var card = cards.FirstOrDefault(item => item.Notice.Key == key);
        if (card is null) return;
        cards.Remove(card); card.Close();
    }
    private void Arrange()
    {
        if (arranging) return;
        arranging = true;
        try
        {
        double offset = 0;
        foreach (var card in cards.AsEnumerable().Reverse())
        { card.StackOffset = offset; card.Place(); offset += card.ActualHeight + 12; }
        GlassSnapshot.Refresh(cards.Where(card => card.IsVisible && card.ActualHeight > 0).ToArray());
        }
        finally { arranging = false; }
    }
    internal void Hide() { foreach (var card in cards.ToArray()) card.Close(); cards.Clear(); }
    public void Dispose() => Hide();
}
