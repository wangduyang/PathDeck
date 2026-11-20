using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace QuickFileDesk;

// Keep the highlight behind content, with no background rendering loop.
public sealed class LiquidGlassBorder : Border
{
    private static readonly DependencyProperty HighlightPositionProperty = DependencyProperty.Register(
        "HighlightPosition", typeof(Point), typeof(LiquidGlassBorder),
        new FrameworkPropertyMetadata(new Point(.25, 0), FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly DependencyProperty HighlightOpacityProperty = DependencyProperty.Register(
        "HighlightOpacity", typeof(double), typeof(LiquidGlassBorder),
        new FrameworkPropertyMetadata(.16, FrameworkPropertyMetadataOptions.AffectsRender));
    private readonly RadialGradientBrush highlight = new()
    {
        GradientStops =
        {
            new GradientStop(Color.FromArgb(150, 255, 255, 255), 0),
            new GradientStop(Color.FromArgb(36, 166, 245, 223), .5),
            new GradientStop(Colors.Transparent, 1)
        }
    };

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (ActualWidth <= 1 || ActualHeight <= 1 || SystemParameters.HighContrast) return;
        var bounds = new Rect(1, 1, ActualWidth - 2, ActualHeight - 2);
        var radius = Math.Max(0, Math.Min(CornerRadius.TopLeft - 1, bounds.Height / 2));
        dc.PushClip(new RectangleGeometry(bounds, radius, radius));
        if (TryFindResource("GlassSheen") is Brush sheen)
            dc.DrawRectangle(sheen, null, bounds);
        highlight.Center = highlight.GradientOrigin = (Point)GetValue(HighlightPositionProperty);
        highlight.RadiusX = Math.Max(.3, 140 / ActualWidth);
        highlight.RadiusY = Math.Max(.65, 80 / ActualHeight);
        highlight.Opacity = (double)GetValue(HighlightOpacityProperty);
        dc.DrawRectangle(highlight, null, bounds);
        dc.Pop();
    }

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        Animate(HighlightOpacityProperty, new DoubleAnimation(.58, TimeSpan.FromMilliseconds(180)));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        var point = e.GetPosition(this);
        Animate(HighlightPositionProperty, new PointAnimation(
            new Point(Math.Clamp(point.X / ActualWidth, 0, 1), Math.Clamp(point.Y / ActualHeight, 0, 1)),
            TimeSpan.FromMilliseconds(140)));
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        Animate(HighlightOpacityProperty, new DoubleAnimation(.16, TimeSpan.FromMilliseconds(220)));
        Animate(HighlightPositionProperty, new PointAnimation(new Point(.25, 0), TimeSpan.FromMilliseconds(220)));
    }

    private void Animate(DependencyProperty property, AnimationTimeline animation)
    {
        if (!SystemParameters.ClientAreaAnimation || SystemParameters.HighContrast)
        {
            BeginAnimation(property, null);
            SetValue(property, animation is PointAnimation point ? (object)point.To!.Value : ((DoubleAnimation)animation).To!.Value);
            return;
        }
        BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
    }
}