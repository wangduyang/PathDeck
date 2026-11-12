using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace QuickFileDesk;

public partial class ColorPickerWindow : Window
{
    private static readonly Color[] Presets =
    [
        Color.FromRgb(255, 100, 53), Color.FromRgb(224, 53, 67), Color.FromRgb(251, 169, 47),
        Color.FromRgb(255, 219, 67), Color.FromRgb(91, 193, 64), Color.FromRgb(16, 160, 126),
        Color.FromRgb(30, 186, 220), Color.FromRgb(44, 111, 222), Color.FromRgb(123, 83, 216),
        Color.FromRgb(224, 79, 164), Colors.White, Colors.Black
    ];

    private double hue;
    private double saturation;
    private double brightness;
    private bool updating;
    internal Color SelectedColor { get; private set; }

    internal ColorPickerWindow(Color initial, IReadOnlyList<Color> recent)
    {
        InitializeComponent();
        SetColor(initial);
        foreach (var color in Presets) AddSwatch(PresetSwatches, color);
        foreach (var color in recent) AddSwatch(RecentSwatches, color);
        RecentSection.Visibility = recent.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void FocusHex(object sender, RoutedEventArgs e)
    {
        HexInput.Focus();
        HexInput.SelectAll();
    }

    private void AddSwatch(Panel panel, Color color)
    {
        var value = Hex(color);
        var button = new Button
        {
            Background = new SolidColorBrush(color), Padding = new Thickness(0),
            Width = 27, MinHeight = 0, Height = 25, Margin = new Thickness(0, 0, 4, 3), ToolTip = value
        };
        button.SetResourceReference(FrameworkElement.StyleProperty, "DialogButton");
        AutomationProperties.SetName(button, value);
        button.Click += (_, _) => SetColor(color);
        panel.Children.Add(button);
    }

    private static string Hex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private void SetColor(Color color, TextBox? editing = null)
    {
        var red = color.R / 255d;
        var green = color.G / 255d;
        var blue = color.B / 255d;
        brightness = Math.Max(red, Math.Max(green, blue));
        var spread = brightness - Math.Min(red, Math.Min(green, blue));
        saturation = brightness > 0 ? spread / brightness : 0;
        if (spread > 0)
        {
            var sector = brightness == red ? (green - blue) / spread :
                brightness == green ? (blue - red) / spread + 2 : (red - green) / spread + 4;
            hue = ((sector + 6) % 6) * 60;
        }
        ShowColor(color, editing);
    }

    private void ShowColor(Color color, TextBox? editing = null)
    {
        updating = true;
        SelectedColor = color;
        if (editing != HexInput) HexInput.Text = Hex(color);
        if (editing != RedInput) RedInput.Text = color.R.ToString(CultureInfo.InvariantCulture);
        if (editing != GreenInput) GreenInput.Text = color.G.ToString(CultureInfo.InvariantCulture);
        if (editing != BlueInput) BlueInput.Text = color.B.ToString(CultureInfo.InvariantCulture);
        PreviewColor.Background = new SolidColorBrush(color);
        PlaneColor.Background = new SolidColorBrush(FromHsv(hue, 1, 1));
        PositionMarkers();
        SetValidity(true);
        updating = false;
    }

    private void ColorInputChanged(object sender, TextChangedEventArgs e)
    {
        if (updating || AcceptButton is null || sender is not TextBox field) return;
        if (field == HexInput)
        {
            var text = HexInput.Text.Trim().TrimStart('#');
            if (text.Length == 6 && uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
            {
                SetColor(Color.FromRgb((byte)(value >> 16), (byte)(value >> 8), (byte)value), field);
                return;
            }
        }
        else if (TryReadChannel(RedInput, out var red) && TryReadChannel(GreenInput, out var green) &&
                 TryReadChannel(BlueInput, out var blue))
        {
            SetColor(Color.FromRgb(red, green, blue), field);
            return;
        }
        SetValidity(false);
    }

    private static bool TryReadChannel(TextBox field, out byte channel) =>
        byte.TryParse(field.Text, NumberStyles.None, CultureInfo.InvariantCulture, out channel);

    private void SetValidity(bool valid)
    {
        AcceptButton.IsEnabled = valid;
        InputError.Visibility = valid ? Visibility.Collapsed : Visibility.Visible;
    }

    private void StartColorDrag(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement area || !area.CaptureMouse()) return;
        UpdateFromPointer(area, e.GetPosition(area));
        e.Handled = true;
    }

    private void MoveColorDrag(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement { IsMouseCaptured: true } area && e.LeftButton == MouseButtonState.Pressed)
            UpdateFromPointer(area, e.GetPosition(area));
    }

    private void EndColorDrag(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { IsMouseCaptured: true } area) return;
        UpdateFromPointer(area, e.GetPosition(area));
        area.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void UpdateFromPointer(FrameworkElement area, Point point)
    {
        var y = Math.Clamp(point.Y / Math.Max(1, area.ActualHeight), 0, 1);
        if (area == HueStrip) hue = y * 360;
        else
        {
            saturation = Math.Clamp(point.X / Math.Max(1, area.ActualWidth), 0, 1);
            brightness = 1 - y;
        }
        ShowColor(FromHsv(hue, saturation, brightness));
    }

    private void PickerResized(object sender, SizeChangedEventArgs e) => PositionMarkers();

    private void PositionMarkers()
    {
        if (PlaneMarker is null || HueMarker is null) return;
        Canvas.SetLeft(PlaneMarker, Math.Clamp(ColorPlane.ActualWidth * saturation - 6, 0, Math.Max(0, ColorPlane.ActualWidth - 12)));
        Canvas.SetTop(PlaneMarker, Math.Clamp(ColorPlane.ActualHeight * (1 - brightness) - 6, 0, Math.Max(0, ColorPlane.ActualHeight - 12)));
        Canvas.SetTop(HueMarker, Math.Clamp(HueStrip.ActualHeight * hue / 360 - 3.5, 0, Math.Max(0, HueStrip.ActualHeight - 7)));
    }

    private static Color FromHsv(double hue, double saturation, double value)
    {
        var sector = hue % 360 / 60;
        var chroma = value * saturation;
        var second = chroma * (1 - Math.Abs(sector % 2 - 1));
        var (r, g, b) = (int)sector switch
        {
            0 => (chroma, second, 0d),
            1 => (second, chroma, 0d),
            2 => (0d, chroma, second),
            3 => (0d, second, chroma),
            4 => (second, 0d, chroma),
            _ => (chroma, 0d, second)
        };
        var offset = value - chroma;
        return Color.FromRgb((byte)Math.Round((r + offset) * 255), (byte)Math.Round((g + offset) * 255),
            (byte)Math.Round((b + offset) * 255));
    }

    private void AcceptColor(object sender, RoutedEventArgs e) => DialogResult = true;
}
