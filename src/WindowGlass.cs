using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using TaskGlass;

namespace QuickFileDesk;

// The shader and blur apply only to this background layer, never to text or image pixels.
internal sealed class WindowGlass
{
    private readonly SharedAppearance appearance;
    private readonly BackdropImage background = new();
    private readonly Border materialLayer = new() { IsHitTestVisible = false };
    private readonly BlurEffect blur = new();
    private readonly GlassTintEffect tint = new();
    internal Grid Layers { get; } = new();

    internal WindowGlass(Window window, SharedAppearance appearance)
    {
        this.appearance = appearance;
        if (window.Content is not UIElement content) return;
        window.Content = null;
        background.Effect = blur;
        materialLayer.Child = background; materialLayer.Effect = tint;
        Layers.Children.Add(materialLayer); Layers.Children.Add(content);
        window.Content = Layers;
        window.SetCurrentValue(Window.BackgroundProperty, Brushes.Transparent);
        appearance.Changed += Apply;
        background.SnapshotChanged += () => materialLayer.Background = background.Source is null ? CurrentMaterial().Fallback : Brushes.Transparent;
        background.Attach(window);
        window.Closed += (_, _) => appearance.Changed -= Apply;
        Apply();
    }

    private GlassMaterial CurrentMaterial() => GlassTheme.Material(appearance.Preferences.TaskbarColor, appearance.Preferences.Opacity);
    internal void Prepare()=>background.Prepare();
    internal void Refresh()=>background.Refresh();
    private void Apply()
    {
        var material = CurrentMaterial(); tint.Apply(material);
        blur.Radius = appearance.Preferences.Blur * .6;
        if (background.Source is null) materialLayer.Background = material.Fallback;
    }
}
