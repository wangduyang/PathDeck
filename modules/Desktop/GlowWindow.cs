using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace TaskGlass;

internal sealed class GlowWindow : Window
{
    private readonly GlassEdge edge = new();
    private nint handle;
    private string color = GlassTheme.TaskbarTint;
    internal void SetColor(string value)
    {
        if (color == value) return;
        color = value; edge.Rim = GlassTheme.TaskbarRim(value); edge.InvalidateVisual();
    }
    internal GlowWindow()
    {
        Title = "PathDeck 背景高光";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false; ShowActivated = false; Focusable = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -30000; Top = -30000; Width = 1; Height = 1;
        Content = edge;
        SourceInitialized += (_, _) =>
        {
            handle = new WindowInteropHelper(this).Handle;
            long style = Native.GetWindowLong(handle, -20).ToInt64();
            Native.SetWindowLong(handle, -20, (nint)(style | 0x20 | 0x80000 | 0x08000000 | 0x80));
            HwndSource.FromHwnd(handle)?.AddHook((nint hwnd, int message, nint wp, nint lp, ref bool handled) =>
            {
                if (message == 0x0084) { handled = true; return -1; }
                if (message == 0x0021) { handled = true; return 3; }
                return 0;
            });
        };
    }

    internal void Place(Native.Rect bounds, double radius)
    {
        if (!IsVisible) Show();
        edge.Radius = radius;
        Native.SetWindowPos(handle, -1, bounds.Left, bounds.Top, bounds.Width, bounds.Height, 0x0010 | 0x0040);
        edge.InvalidateVisual();
    }

    internal void MoveLight(double x)
    {
        if (!GlassMotion.Active)
        {
            if (edge.LightX == .4 && edge.Strength == .24) return;
            edge.LightX = .4; edge.Strength = .24; edge.InvalidateVisual(); return;
        }
        if (Math.Abs(edge.LightX - x) < .002) return;
        edge.LightX = x; edge.InvalidateVisual();
    }
    internal void SetHover(bool value)
    {
        bool animation = GlassMotion.Active;
        double strength = animation && value ? .46 : .24;
        double x = animation ? edge.LightX : .4;
        if (edge.Strength == strength && edge.LightX == x) return;
        edge.Strength = strength; edge.LightX = x; edge.InvalidateVisual();
    }

    private sealed class GlassEdge : FrameworkElement
    {
        internal double Radius = 16;
        internal double LightX = .4;
        internal double Strength = .24;
        internal Brush Rim = GlassTheme.TaskbarRim();
        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            if (ActualWidth < 2 || ActualHeight < 2) return;
            double pixel = 1 / VisualTreeHelper.GetDpi(this).DpiScaleX;
            var rect = new Rect(pixel, pixel, ActualWidth - 2 * pixel, ActualHeight - 2 * pixel);
            double radius = Math.Max(0, Math.Min(Radius, ActualHeight / 2) - pixel);
            // The center remains transparent so native icons retain their contrast.
            // Opaque narrow rim covers the hard native clip; only its outer contour uses fractional alpha.
            dc.DrawRoundedRectangle(null, new Pen(Rim, 2 * pixel), rect, radius, radius);
            var light = new RadialGradientBrush(GlassTheme.Highlight, Colors.Transparent)
            { Center = new Point(LightX, 0), GradientOrigin = new Point(LightX, 0), RadiusX = Math.Max(.06, 180 / ActualWidth), RadiusY = .7 };
            dc.PushOpacity(Strength);
            dc.DrawRoundedRectangle(null, new Pen(light, 2 * pixel), rect, radius, radius);
            dc.Pop();
        }
    }
}
