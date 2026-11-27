using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace TaskGlass;

internal sealed class TaskbarClockWindow : Window
{
    private readonly Image sample = new() { Stretch = Stretch.Fill, IsHitTestVisible = false };
    private readonly BlurEffect blur = new() { RenderingBias = RenderingBias.Performance };
    private readonly GlassTintEffect tintEffect = new();
    private readonly Border surface;
    private readonly Border dayProgress = new() { IsHitTestVisible=false };
    private readonly Border progressShade = new() { IsHitTestVisible=false };
    private readonly Border progressLight = new() { IsHitTestVisible=false };
    private readonly RectangleGeometry progressClip = new();
    private readonly RectangleGeometry futureTextClip = new();
    private double dayFraction=-1;
    private readonly System.Windows.Shapes.Path timeIcon = CreateTimeIcon(),pastTimeIcon=CreateTimeIcon();
    private static System.Windows.Shapes.Path CreateTimeIcon()=>new()
    {
        Data = Geometry.Parse("M 9,1 A 8,8 0 1 1 8.999,1 M 9,4.5 L 9,9 L 12.5,11"),
        Width = 20, Height = 20, Stretch=Stretch.Uniform, StrokeThickness = 1.8, StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
        Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
        IsHitTestVisible = false
    };
    private readonly Grid layers = new() { UseLayoutRounding = true, SnapsToDevicePixels = true };
    private readonly TextBlock clock = CreateClock(),pastClock=CreateClock();
    private static TextBlock CreateClock()=>new()
    {
        FontFamily = new FontFamily("Segoe UI"), FontSize = 20,
        FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.NoWrap, TextAlignment = TextAlignment.Center
    };
    private readonly DispatcherTimer tickTimer = new(DispatcherPriority.Normal);
    private readonly DispatcherTimer sampleTimer = new() { Interval = TimeSpan.FromMilliseconds(80) };
    private Preferences appearance = new();
    private Native.Rect bounds;
    private nint handle;

    internal string TimeText => clock.Text;
    internal bool Live => IsVisible && tickTimer.IsEnabled;
    internal static string Format(DateTime value) => value.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
    internal static double DayProgress(DateTime value) => (value.Hour*60+value.Minute)/1440.0;

    internal TaskbarClockWindow()
    {
        Title = "PathDeck 时钟"; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true; Background = Brushes.Transparent; Topmost = true;
        ShowInTaskbar = false; ShowActivated = false; Focusable = false; IsHitTestVisible = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -30000; Top = -30000; Width = 1; Height = 1;
        foreach(var text in new[]{clock,pastClock})
        {
            Typography.SetNumeralAlignment(text, FontNumeralAlignment.Tabular);
            TextOptions.SetTextFormattingMode(text, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(text, TextRenderingMode.Grayscale);
        }
        sample.Effect = blur;
        layers.Children.Add(new Border { Child = sample, Effect = tintEffect, IsHitTestVisible = false });
        surface = new Border { BorderThickness = new Thickness(1), IsHitTestVisible = false };
        layers.Children.Add(surface);
        dayProgress.Clip=progressClip;layers.Children.Add(dayProgress);
        var progressLighting=new Grid();progressLighting.Children.Add(progressShade);progressLighting.Children.Add(progressLight);dayProgress.Child=progressLighting;
        AutomationProperties.SetName(clock,"当前时间与一天进度");
        var futureText=CreateTimeLayer(clock,timeIcon);futureText.Clip=futureTextClip;layers.Children.Add(futureText);
        var pastText=CreateTimeLayer(pastClock,pastTimeIcon);pastText.Clip=progressClip;layers.Children.Add(pastText);
        Content = layers;
        layers.SizeChanged += (_, _) =>
        {
            UpdateShape();
            ScheduleSample();
        };
        SourceInitialized += (_, _) =>
        {
            handle = new WindowInteropHelper(this).Handle;
            if (HwndSource.FromHwnd(handle)?.CompositionTarget is { } target) target.BackgroundColor = Colors.Transparent;
            long style = Native.GetWindowLong(handle, -20).ToInt64();
            Native.SetWindowLong(handle, -20, (nint)(style | 0x20 | 0x80000 | 0x08000000 | 0x80));
            HwndSource.FromHwnd(handle)?.AddHook((nint hwnd, int message, nint wp, nint lp, ref bool handled) =>
            {
                if (message == 0x0084) { handled = true; return -1; }
                if (message == 0x0021) { handled = true; return 3; }
                return 0;
            });
        };
        tickTimer.Tick += (_, _) => UpdateTime(DateTime.Now);
        sampleTimer.Tick += (_, _) =>
        {
            sampleTimer.Stop();
            if (IsVisible) { sample.Source = GlassSnapshot.Capture(this); ApplyMaterial(); }
        };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) { UpdateTime(DateTime.Now); tickTimer.Start(); }
            else ReleaseBackdrop();
        };
        Closed += (_, _) => ReleaseBackdrop();
        ApplyAppearance(appearance); UpdateTime(DateTime.Now);
    }
    private static Grid CreateTimeLayer(TextBlock text,System.Windows.Shapes.Path icon)
    {
        var content=new StackPanel{Orientation=Orientation.Horizontal,IsHitTestVisible=false};content.Children.Add(icon);content.Children.Add(text);
        var layer=new Grid{IsHitTestVisible=false};
        layer.Children.Add(new Viewbox
        {
            Child=content,Stretch=Stretch.Uniform,StretchDirection=StretchDirection.DownOnly,
            Margin=new Thickness(18,4,18,4),HorizontalAlignment=HorizontalAlignment.Stretch,
            VerticalAlignment=VerticalAlignment.Stretch,IsHitTestVisible=false
        });
        return layer;
    }

    internal void Place(Native.Rect value, Preferences preferences)
    {
        if (value.Width <= 0 || value.Height <= 0) { HideClock(); return; }
        bool fresh = !IsVisible, moved = !bounds.Equals(value);
        bool changed = appearance.TaskbarColor != preferences.TaskbarColor || appearance.Blur != preferences.Blur || appearance.Opacity != preferences.Opacity || appearance.Radius != preferences.Radius;
        if (changed || appearance.WorkStart!=preferences.WorkStart || appearance.WorkEnd!=preferences.WorkEnd) ApplyAppearance(preferences);
        bounds = value;
        if (fresh) Show();
        if (fresh || moved)
            Native.SetWindowPos(handle, -1, value.Left, value.Top, value.Width, value.Height, 0x0010 | 0x0040);
        if (fresh || moved || changed) ScheduleSample();
    }

    internal void HideClock()
    {
        if (IsVisible) Hide();
        ReleaseBackdrop();
    }

    private void UpdateTime(DateTime now)
    {
        string value = Format(now);
        if (clock.Text != value) {clock.Text = value;pastClock.Text=value;}
        double fraction=DayProgress(now);
        if(dayFraction!=fraction){dayFraction=fraction;UpdateProgress();}
        tickTimer.Interval = TimeSpan.FromMilliseconds(1000 - now.Millisecond);
    }
    private void UpdateProgress()
    {
        if(dayFraction<0)return;
        double width=layers.ActualWidth,height=layers.ActualHeight;
        progressClip.Rect=new Rect(0,0,width*dayFraction,height);
        futureTextClip.Rect=new Rect(width*dayFraction,0,width*(1-dayFraction),height);
        AutomationProperties.SetHelpText(clock,$"今日已过 {dayFraction:P0}，工作时段 {appearance.WorkStart} 至 {appearance.WorkEnd}");
    }

    private void ApplyAppearance(Preferences preferences)
    {
        appearance = preferences;
        blur.Radius = preferences.Blur * .6;
        surface.BorderBrush = GlassTheme.TaskbarRim(preferences.TaskbarColor);
        ApplyMaterial(); UpdateShape();
    }

    private void UpdateShape()
    {
        double radius = Math.Min(appearance.Radius * layers.ActualHeight / 48, layers.ActualHeight / 2);
        layers.Clip = new RectangleGeometry(new Rect(0, 0, layers.ActualWidth, layers.ActualHeight), radius, radius);
        surface.CornerRadius = new CornerRadius(radius);
        surface.BorderThickness = new Thickness(1 / VisualTreeHelper.GetDpi(this).DpiScaleX);
        UpdateProgress();
    }

    private void ApplyMaterial()
    {
        var material = GlassTheme.Material(appearance.TaskbarColor, appearance.Opacity);
        tintEffect.Apply(material);
        surface.Background = sample.Source is null ? material.Fallback : Brushes.Transparent;
        clock.Foreground = new SolidColorBrush(material.Foreground);
        timeIcon.Stroke = clock.Foreground;
        pastClock.Foreground=Brushes.Black;pastTimeIcon.Stroke=Brushes.Black;
        var outside=Color.FromArgb(235,65,209,123);
        var working=Color.FromArgb(235,155,245,100);
        double start=TimeOnly.ParseExact(appearance.WorkStart,"HH:mm",CultureInfo.InvariantCulture).ToTimeSpan().TotalMinutes;
        double end=TimeOnly.ParseExact(appearance.WorkEnd,"HH:mm",CultureInfo.InvariantCulture).ToTimeSpan().TotalMinutes;
        var shoulder=GlassTheme.Mix(outside,working,.45);shoulder.A=235;
        var fill=new LinearGradientBrush {StartPoint=new Point(0,0),EndPoint=new Point(1,0),ColorInterpolationMode=ColorInterpolationMode.ScRgbLinearInterpolation};
        fill.GradientStops.Add(new GradientStop(outside,0));
        fill.GradientStops.Add(new GradientStop(shoulder,start/1440));
        fill.GradientStops.Add(new GradientStop(working,(start+end)/2880));
        fill.GradientStops.Add(new GradientStop(shoulder,end/1440));
        fill.GradientStops.Add(new GradientStop(outside,1));fill.Freeze();
        dayProgress.Background=fill;
        var shade=new LinearGradientBrush(Colors.Transparent,Color.FromArgb(24,0,45,20),90);
        shade.Freeze();progressShade.Background=shade;
        var light=new RadialGradientBrush(Color.FromArgb(40,255,255,255),Colors.Transparent)
        {Center=new Point(0,0),GradientOrigin=new Point(0,0),RadiusX=.9,RadiusY=.95};
        light.Freeze();progressLight.Background=light;
        UpdateProgress();
    }

    private void ScheduleSample()
    {
        sampleTimer.Stop();
        if (IsVisible) sampleTimer.Start();
    }

    private void ReleaseBackdrop()
    {
        tickTimer.Stop(); sampleTimer.Stop(); sample.Source = null; ApplyMaterial();
    }
}
