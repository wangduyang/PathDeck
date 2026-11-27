using System;
using System.Windows;
using System.Windows.Media;

namespace TaskGlass;

internal static class GlassTheme
{
    internal const string TaskbarTint = "#242B2D";
    internal const string NotificationTint = "#F7F9F9";
    internal static string NormalizeColor(string? value, string fallback)
    {
        if (value is null || value.Length != 7 || value[0] != '#') return fallback;
        for (int i = 1; i < value.Length; i++) if (!Uri.IsHexDigit(value[i])) return fallback;
        return value.ToUpperInvariant();
    }
    internal static Color Mix(Color color, Color other, double amount) => Color.FromRgb(
        (byte)Math.Round(color.R * (1 - amount) + other.R * amount),
        (byte)Math.Round(color.G * (1 - amount) + other.G * amount),
        (byte)Math.Round(color.B * (1 - amount) + other.B * amount));
    internal static double Luminance(Color color)
    {
        static double Channel(byte value) { double s = value / 255.0; return s <= .04045 ? s / 12.92 : Math.Pow((s + .055) / 1.055, 2.4); }
        return .2126 * Channel(color.R) + .7152 * Channel(color.G) + .0722 * Channel(color.B);
    }
    internal static GlassMaterial Material(string value, double opacity)
    {
        var tint = (Color)ColorConverter.ConvertFromString(value);
        var palette = NoticePalette(value);
        double alpha = Math.Clamp(opacity / 100, .1, .8);
        for (int step = 0; step <= 20; step++)
        {
            var material = new GlassMaterial(tint, palette.Foreground, alpha, step / 20.0, tint);
            var worst = Worst(material);
            if (Contrast(palette.Foreground, worst) >= 4.7) return material with { WorstBackground = worst };
        }
        // Extreme custom tints may need more color coverage to retain readable text.
        for (int a = Math.Max((int)Math.Ceiling(alpha * 255), palette.Background.GradientStops[1].Color.A); a <= 255; a++)
        {
            var material = new GlassMaterial(tint, palette.Foreground, a / 255.0, 0, tint);
            var worst = Worst(material);
            if (Contrast(palette.Foreground, worst) >= 4.5) return material with { WorstBackground = worst };
        }
        return new GlassMaterial(tint, palette.Foreground, 1, 0, tint);
    }
    private static double Contrast(Color foreground, Color background)
    {
        double one = Luminance(foreground), two = Luminance(background);
        return (Math.Max(one, two) + .05) / (Math.Min(one, two) + .05);
    }
    private static Color Worst(GlassMaterial material)
    {
        var worst = material.Tint; double ratio = double.MaxValue;
        foreach (byte r in new byte[] { 0, 64, 128, 192, 255 })
            foreach (byte g in new byte[] { 0, 64, 128, 192, 255 })
                foreach (byte b in new byte[] { 0, 64, 128, 192, 255 })
                {
                    var color = material.Composite(Color.FromRgb(r, g, b));
                    double next = Contrast(material.Foreground, color);
                    if (next < ratio) { ratio = next; worst = color; }
                }
        return worst;
    }
    internal static (LinearGradientBrush Background, Color Foreground) NoticePalette(string value)
    {
        var color = (Color)ColorConverter.ConvertFromString(value);
        for (int alpha = 164; alpha <= 255; alpha++)
        {
            double opacity = alpha / 255.0;
            double darkText = (Luminance(Mix(Colors.Black, color, opacity)) + .05) / .05;
            double lightText = 1.05 / (Luminance(Mix(Colors.White, color, opacity)) + .05);
            if (Math.Max(darkText, lightText) < 4.5) continue;
            var top = color; top.A = (byte)Math.Min(255, alpha + 18);
            var bottom = color; bottom.A = (byte)alpha;
            return (new LinearGradientBrush(top, bottom, 85), darkText >= lightText ? Colors.Black : Colors.White);
        }
        return (new LinearGradientBrush(color,color,85), Luminance(color) > .179 ? Colors.Black : Colors.White);
    }
    internal static readonly Color Highlight = Color.FromArgb(160, 232, 245, 239);
    // Layered controls must strengthen ink contrast instead of whitening a dark glass backdrop.
    internal static SolidColorBrush Surface(GlassMaterial material) => new(material.Foreground == Colors.White
        ? Color.FromArgb(24, 0, 0, 0) : Color.FromArgb(24, 255, 255, 255));
    internal static LinearGradientBrush TaskbarRim(string value = TaskbarTint)
    {
        var color = (Color)ColorConverter.ConvertFromString(value);
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        if (value != TaskbarTint)
        {
            brush.GradientStops.Add(new GradientStop(Mix(color,Colors.White,.16),0));
            brush.GradientStops.Add(new GradientStop(Mix(color,Colors.Black,.12),.5));
            brush.GradientStops.Add(new GradientStop(Mix(color,Colors.White,.02),1));
            brush.Freeze(); return brush;
        }
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(71, 79, 81), 0));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(32, 39, 41), .5));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(39, 46, 48), 1));
        brush.Freeze(); return brush;
    }
    internal static LinearGradientBrush Rim()
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(.8, 1) };
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(116, 255, 255, 255), 0));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(38, 245, 248, 248), .45));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(32, 84, 96, 99), .8));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(66, 250, 252, 252), 1));
        brush.Freeze(); return brush;
    }
}

internal sealed record GlassMaterial(Color Tint, Color Foreground, double TintOpacity, double LuminosityOpacity, Color WorstBackground)
{
    internal const double Saturation = 1.18;
    internal Color Composite(Color source)
    {
        double r = source.R / 255.0, g = source.G / 255.0, b = source.B / 255.0;
        double luminance = .2126 * r + .7152 * g + .0722 * b;
        double tintLuminance = (.2126 * Tint.R + .7152 * Tint.G + .0722 * Tint.B) / 255;
        double shift = (tintLuminance - luminance) * LuminosityOpacity;
        // Match the native effect order; clamp only after saturation, luminosity and tint.
        byte Channel(double value, byte tint) => (byte)Math.Clamp(Math.Round(((luminance + (value - luminance) * Saturation + shift) * (1 - TintOpacity) + tint / 255.0 * TintOpacity) * 255), 0, 255);
        return Color.FromArgb(source.A, Channel(r, Tint.R), Channel(g, Tint.G), Channel(b, Tint.B));
    }
    internal Brush Fallback => new SolidColorBrush(Tint);
}
