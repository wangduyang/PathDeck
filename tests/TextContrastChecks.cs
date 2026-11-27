using System.Windows;
using System.Windows.Media;
using QuickFileDesk;
using TaskGlass;

internal static class TextContrastChecks
{
    internal static Color Over(Color ink, Color background, double opacity = 1)
    {
        double alpha = ink.A / 255.0 * opacity;
        byte Channel(byte one, byte two) => (byte)Math.Round(one * alpha + two * (1 - alpha));
        return Color.FromRgb(Channel(ink.R, background.R), Channel(ink.G, background.G), Channel(ink.B, background.B));
    }
    internal static double Ratio(Color one, Color two) =>
        (Math.Max(GlassTheme.Luminance(one), GlassTheme.Luminance(two)) + .05) /
        (Math.Min(GlassTheme.Luminance(one), GlassTheme.Luminance(two)) + .05);

    internal static void Run(App app)
    {
        using var appearance = new SharedAppearance(app);
        foreach (string tint in new[] { "#242B2D", "#FFFFFF", "#737373", "#747474", "#757575", "#777777", "#563875", "#FF0000", "#00FF00", "#0000FF" })
        foreach (double opacity in new[] { 10d, 41d, 80d })
        {
            var preferences = new Preferences { TaskbarColor = tint, Opacity = opacity };
            appearance.Apply(preferences);
            var material = GlassTheme.Material(tint, opacity);
            foreach (string text in new[] { "Text", "Muted" })
            foreach (string surface in new[] { "Panel", "SidebarCard", "Field", "Chrome", "ToolbarSurface", "OverviewTile", "SearchSurface" })
            {
                var brush = (SolidColorBrush)app.Resources[text];
                var panel = (SolidColorBrush)app.Resources[surface];
                for (int r = 0; r <= 255; r += 51) for (int g = 0; g <= 255; g += 51) for (int b = 0; b <= 255; b += 51)
                {
                    var background = material.Composite(Color.FromRgb((byte)r, (byte)g, (byte)b));
                    background = Over(panel.Color, background, panel.Opacity);
                    double ratio = Ratio(Over(brush.Color, background, brush.Opacity), background);
                    if (ratio < 4.49) throw new Exception($"Composited {text} contrast {ratio:0.00}:1 on {surface}, {tint}/{opacity}; include brush opacity.");
                }
                // The moving sheen is drawn behind the same text, at its strongest hover state.
                var worst = Over(panel.Color, material.WorstBackground, panel.Opacity);
                var sheen = (LinearGradientBrush)app.Resources["GlassSheen"];
                worst = Over(sheen.GradientStops[0].Color, worst, sheen.Opacity);
                var border = new LiquidGlassBorder();
                var highlight = (RadialGradientBrush)typeof(LiquidGlassBorder).GetField("highlight", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(border)!;
                var highlightColor = app.Resources["GlassHighlightColor"] is Color color ? color : highlight.GradientStops[0].Color;
                worst = Over(highlightColor, worst, .58);
                if (Ratio(Over(brush.Color, worst, brush.Opacity), worst) < 4.49)
                    throw new Exception($"Hovered glass must preserve {text} contrast, {tint}/{opacity}, {surface}.");
            }
        }
        Console.WriteLine("PASS: body and secondary text final composited contrast across 10 tints, 3 opacities, 7 surfaces and RGB backdrops, including hover and light/dark boundary; brush alpha and opacity included.");
    }
}
