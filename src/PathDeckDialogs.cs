using System.Windows;
using System.Windows.Controls;

namespace QuickFileDesk;

internal static class PathDeckDialogs
{
    public static Window Create(Window owner, string title, double width)
    {
        var dialog = new Window { Title = title, Width = width };
        dialog.SetResourceReference(FrameworkElement.StyleProperty, "DialogWindow");
        Attach(dialog, owner);
        return dialog;
    }

    public static void Attach(Window dialog, Window owner)
    {
        dialog.Owner = owner;
        dialog.Icon = owner.Icon;
        dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        (Application.Current as App)?.TrackWindowTheme(dialog);
    }

    public static bool Confirm(Window owner, string title, string message, string confirm, string cancel) =>
        ShowMessage(owner, title, message, confirm, cancel);

    public static void Notice(Window owner, string title, string message) =>
        ShowMessage(owner, title, message, null, "确定");

    private static bool ShowMessage(Window owner, string title, string message, string? confirm, string cancel)
    {
        var dialog = Create(owner, "PathDeck", 410);
        var heading = new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        var body = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        body.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        var card = new Border { Child = new StackPanel { Children = { heading, body } } };
        card.SetResourceReference(FrameworkElement.StyleProperty, "DialogCard");

        var keep = new Button { Content = cancel, MinWidth = 78, IsDefault = true, IsCancel = true };
        keep.SetResourceReference(FrameworkElement.StyleProperty, "DialogPrimaryButton");
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0), Children = { keep }
        };
        if (confirm is not null)
        {
            var discard = new Button { Content = confirm, Margin = new Thickness(8, 0, 0, 0) };
            discard.SetResourceReference(FrameworkElement.StyleProperty, "DialogDangerButton");
            discard.Click += (_, _) => dialog.DialogResult = true;
            actions.Children.Add(discard);
        }
        dialog.Content = new StackPanel { Margin = new Thickness(16), Children = { card, actions } };
        dialog.Loaded += (_, _) => keep.Focus();
        return dialog.ShowDialog() == true;
    }
}
