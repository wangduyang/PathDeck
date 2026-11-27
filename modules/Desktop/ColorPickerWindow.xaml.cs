using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace TaskGlass;

public partial class ColorPickerWindow : Window
{
    internal string SelectedColor { get; private set; }
    private bool updating;
    private double saturation, brightness;
    private readonly GlassTintEffect tintEffect = new();
    internal ColorPickerWindow(string color, string scope, Preferences preferences)
    {
        SelectedColor = color; InitializeComponent(); ScopeText.Text = scope;
        Height = Math.Min(780, Math.Max(1, SystemParameters.WorkArea.Height - 24));
        BackdropMaterial.Effect = tintEffect; ApplyMaterial(preferences);
        OriginalSwatch.Background = (Brush)new BrushConverter().ConvertFromString(color)!;
        SetColor((Color)ColorConverter.ConvertFromString(color));
        ColorField.SizeChanged += (_, _) => { ColorField.Clip=new RectangleGeometry(new Rect(0,0,ColorField.ActualWidth,ColorField.ActualHeight),14,14);PlaceMarker(); };
        SizeChanged += (_, _) => Layers.Clip = new RectangleGeometry(new Rect(0,0,ActualWidth,ActualHeight),28,28);
        Backdrop.SnapshotChanged += () => Tint.Background = Backdrop.Source is null
            ? GlassTheme.Material(preferences.TaskbarColor, preferences.Opacity).Fallback : Brushes.Transparent;
        Backdrop.Attach(this);
    }
    private void ApplyMaterial(Preferences preferences)
    {
        var material = GlassTheme.Material(preferences.TaskbarColor, preferences.Opacity);
        tintEffect.Apply(material); BackdropBlur.Radius = preferences.Blur * .6;
        Tint.Background = Backdrop.Source is null ? material.Fallback : Brushes.Transparent;
        Foreground = new SolidColorBrush(material.Foreground);
        Resources["SettingsTextBrush"] = Foreground; Resources["SettingsMutedBrush"] = Foreground;
        Resources["SettingsSurfaceBrush"] = GlassTheme.Surface(material);
        Resources["SettingsAccent"] = new SolidColorBrush(material.Foreground == Colors.White ? Color.FromRgb(136,226,196) : Color.FromRgb(29,88,66));
        Resources["SettingsAccentText"] = new SolidColorBrush(material.Foreground == Colors.White ? Color.FromRgb(23,59,48) : Colors.White);
        Resources["SettingsErrorBrush"] = new SolidColorBrush(material.Foreground == Colors.White ? Color.FromRgb(255,209,189) : Color.FromRgb(143,43,24));
    }
    private static Color FromHsv(double hue, double saturation, double value)
    {
        double h = (hue % 360) / 60, c = value * saturation, x = c * (1 - Math.Abs(h % 2 - 1)), m = value - c;
        var rgb = h switch { <1 => (c,x,0d), <2 => (x,c,0d), <3 => (0d,c,x), <4 => (0d,x,c), <5 => (x,0d,c), _ => (c,0d,x) };
        return Color.FromRgb((byte)Math.Round((rgb.Item1+m)*255),(byte)Math.Round((rgb.Item2+m)*255),(byte)Math.Round((rgb.Item3+m)*255));
    }
    private void SetColor(Color color, bool updateHsv = true)
    {
        updating = true;
        try
        {
            if(updateHsv)
            {
                double r=color.R/255d,g=color.G/255d,b=color.B/255d,max=Math.Max(r,Math.Max(g,b)),min=Math.Min(r,Math.Min(g,b)),delta=max-min;
                double hue=delta==0?HueSlider.Value:max==r?60*((g-b)/delta%6):max==g?60*((b-r)/delta+2):60*((r-g)/delta+4);
                HueSlider.Value=(hue+360)%360; saturation=max==0?0:delta/max; brightness=max;
            }
            SelectedColor=$"#{color.R:X2}{color.G:X2}{color.B:X2}";
            HexInput.Text=SelectedColor;RedInput.Text=color.R.ToString(CultureInfo.InvariantCulture);GreenInput.Text=color.G.ToString(CultureInfo.InvariantCulture);BlueInput.Text=color.B.ToString(CultureInfo.InvariantCulture);
            NewSwatch.Background=new SolidColorBrush(color);ColorField.Background=new SolidColorBrush(FromHsv(HueSlider.Value,1,1));HueText.Text=$"{HueSlider.Value:0}°";
            ErrorText.Visibility=Visibility.Collapsed;ApplyButton.IsEnabled=true;PlaceMarker();
        }
        finally { updating=false; }
    }
    private void PlaceMarker()
    {
        double x=saturation*ColorField.ActualWidth,y=(1-brightness)*ColorField.ActualHeight;
        Canvas.SetLeft(Marker,x-8);Canvas.SetTop(Marker,y-8);Canvas.SetLeft(MarkerOutline,x-10);Canvas.SetTop(MarkerOutline,y-10);
    }
    private void Invalid(string message) { ErrorText.Text=message;ErrorText.Visibility=Visibility.Visible;ApplyButton.IsEnabled=false; }
    private void ChangeHex(object sender, TextChangedEventArgs e)
    {
        if(updating||!IsInitialized)return;
        string value=HexInput.Text.StartsWith('#')?HexInput.Text:"#"+HexInput.Text;
        value=GlassTheme.NormalizeColor(value,"");
        if(value.Length==0){Invalid("请输入 6 位 HEX 颜色，例如 #88E2C4。");return;}
        SetColor((Color)ColorConverter.ConvertFromString(value));
    }
    private void ChangeRgb(object sender, TextChangedEventArgs e)
    {
        if(updating||!IsInitialized)return;
        if(!byte.TryParse(RedInput.Text,NumberStyles.None,CultureInfo.InvariantCulture,out byte r)||!byte.TryParse(GreenInput.Text,NumberStyles.None,CultureInfo.InvariantCulture,out byte g)||!byte.TryParse(BlueInput.Text,NumberStyles.None,CultureInfo.InvariantCulture,out byte b)){Invalid("R、G、B 均需填写 0–255 的整数。");return;}
        SetColor(Color.FromRgb(r,g,b));
    }
    private void ChangeHue(object sender, RoutedPropertyChangedEventArgs<double> e)
    { if(!updating&&IsInitialized)SetColor(FromHsv(HueSlider.Value,saturation,brightness),updateHsv:false); }
    private void SetField(Point point)
    {
        saturation=Math.Clamp(point.X/Math.Max(1,ColorField.ActualWidth),0,1);brightness=1-Math.Clamp(point.Y/Math.Max(1,ColorField.ActualHeight),0,1);
        SetColor(FromHsv(HueSlider.Value,saturation,brightness),updateHsv:false);
    }
    private void SelectField(object sender,MouseButtonEventArgs e){ColorField.CaptureMouse();SetField(e.GetPosition(ColorField));e.Handled=true;}
    private void MoveField(object sender,MouseEventArgs e){if(ColorField.IsMouseCaptured&&e.LeftButton==MouseButtonState.Pressed)SetField(e.GetPosition(ColorField));}
    private void ReleaseField(object sender,MouseButtonEventArgs e){if(ColorField.IsMouseCaptured)ColorField.ReleaseMouseCapture();}
    private void Preset(object sender,RoutedEventArgs e){if(sender is Button {Tag:string value})SetColor((Color)ColorConverter.ConvertFromString(value));}
    private void Apply(object sender,RoutedEventArgs e){if(ApplyButton.IsEnabled)DialogResult=true;}
    private void Cancel(object sender,RoutedEventArgs e)=>DialogResult=false;
}
