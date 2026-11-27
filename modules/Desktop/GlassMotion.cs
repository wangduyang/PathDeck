using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace TaskGlass;

internal static class GlassMotion
{
    internal static bool Enabled { get; set; } = true;
    private static readonly List<WeakReference<ButtonBase>> buttons = new();
    private static readonly DependencyProperty WiredProperty = DependencyProperty.RegisterAttached("Wired", typeof(bool), typeof(GlassMotion), new PropertyMetadata(false));
    private static bool installed;
    internal static bool Active => Enabled && SystemParameters.ClientAreaAnimation;
    internal static void Configure(bool enabled)
    {
        Enabled = enabled;
        if (!installed)
        {
            installed = true;
            EventManager.RegisterClassHandler(typeof(Button), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) => Wire((Button)sender)));
        }
        if (!Active)
            foreach (var item in buttons.ToArray())
                if (item.TryGetTarget(out var button)) { To(button, UIElement.OpacityProperty, 1, 0); if (button.Template.FindName("Surface", button) is FrameworkElement { RenderTransform: ScaleTransform size }) { To(size, ScaleTransform.ScaleXProperty, 1, 0); To(size, ScaleTransform.ScaleYProperty, 1, 0); } }
                else buttons.Remove(item);
    }
    private static void Wire(Button button)
    {
        if ((bool)button.GetValue(WiredProperty)) return;
        button.ApplyTemplate();
        if (button.Template.FindName("Surface", button) is not FrameworkElement surface) return;
        button.SetValue(WiredProperty, true);
        var scale = new ScaleTransform(1, 1); surface.RenderTransform = scale; surface.RenderTransformOrigin = new Point(.5, .5);
        buttons.Add(new WeakReference<ButtonBase>(button));
        if(buttons.Count%256==0)buttons.RemoveAll(reference=>!reference.TryGetTarget(out _));
        void Reset() { To(scale, ScaleTransform.ScaleXProperty, 1, 120); To(scale, ScaleTransform.ScaleYProperty, 1, 120); }
        button.MouseEnter += (_, _) => To(button, UIElement.OpacityProperty, .94, 120);
        button.MouseLeave += (_, _) => { To(button, UIElement.OpacityProperty, 1, 120); Reset(); };
        button.PreviewMouseLeftButtonDown += (_, _) => { To(scale, ScaleTransform.ScaleXProperty, Active ? .98 : 1, 80); To(scale, ScaleTransform.ScaleYProperty, Active ? .98 : 1, 80); };
        button.PreviewMouseLeftButtonUp += (_, _) => Reset();
        button.LostMouseCapture += (_, _) => Reset();
        button.PreviewKeyUp += (_, e) => { if (e.Key is Key.Space or Key.Enter) Reset(); };
    }
    internal static void To(DependencyObject target, DependencyProperty property, double value, int milliseconds)
    {
        double current = (double)target.GetValue(property);
        if (target is UIElement oldElement) oldElement.BeginAnimation(property, null);
        else if (target is Animatable oldAnimation) oldAnimation.BeginAnimation(property, null);
        target.SetValue(property, value);
        var animation = Active && Math.Abs(current - value) > .0001
            ? new DoubleAnimation(current, value, TimeSpan.FromMilliseconds(milliseconds))
              { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop }
            : null;
        if (target is UIElement element) element.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
        else if (target is Animatable animatable) animatable.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
    }
}
