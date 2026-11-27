using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace TaskGlass;

// Apply to the background parent, after the child's BlurEffect. Foreground controls stay outside it.
internal sealed class GlassTintEffect : ShaderEffect
{
    private static readonly PixelShader shader = new()
    { UriSource = new Uri("/PathDeck.Desktop;component/Assets/GlassMaterial.ps", UriKind.Relative) };

    public static readonly DependencyProperty InputProperty = RegisterPixelShaderSamplerProperty("Input", typeof(GlassTintEffect), 0);
    public static readonly DependencyProperty TintRGProperty = DependencyProperty.Register("TintRG", typeof(Point), typeof(GlassTintEffect),
        new UIPropertyMetadata(new Point(0, 0), PixelShaderConstantCallback(0)));
    public static readonly DependencyProperty TintBOProperty = DependencyProperty.Register("TintBO", typeof(Point), typeof(GlassTintEffect),
        new UIPropertyMetadata(new Point(0, 0), PixelShaderConstantCallback(1)));
    public static readonly DependencyProperty ToneProperty = DependencyProperty.Register("Tone", typeof(Point), typeof(GlassTintEffect),
        new UIPropertyMetadata(new Point(0, GlassMaterial.Saturation), PixelShaderConstantCallback(2)));

    public Brush Input { get => (Brush)GetValue(InputProperty); set => SetValue(InputProperty, value); }
    public Point TintRG { get => (Point)GetValue(TintRGProperty); set => SetValue(TintRGProperty, value); }
    public Point TintBO { get => (Point)GetValue(TintBOProperty); set => SetValue(TintBOProperty, value); }
    public Point Tone { get => (Point)GetValue(ToneProperty); set => SetValue(ToneProperty, value); }

    internal GlassTintEffect()
    {
        PixelShader = shader;
        UpdateShaderValue(InputProperty); UpdateShaderValue(TintRGProperty); UpdateShaderValue(TintBOProperty); UpdateShaderValue(ToneProperty);
    }

    internal void Apply(GlassMaterial material)
    {
        // Points retain literal sRGB values; Color shader constants would convert to scRGB.
        TintRG = new Point(material.Tint.R / 255.0, material.Tint.G / 255.0);
        TintBO = new Point(material.Tint.B / 255.0, material.TintOpacity);
        Tone = new Point(material.LuminosityOpacity, GlassMaterial.Saturation);
    }

    protected override Freezable CreateInstanceCore() => new GlassTintEffect();
}
