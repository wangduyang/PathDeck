using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

internal static class GlassSettingsChecks
{
    const BindingFlags All=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    static Color ColorOf(Brush brush)
    {
        var color=((SolidColorBrush)brush).Color;
        color.A=(byte)Math.Round(color.A*brush.Opacity);
        return color;
    }
    static double Ratio(Color one,Color two)
    {
        static double L(Color color)
        {
            static double C(byte channel){double value=channel/255.0;return value<=.04045?value/12.92:Math.Pow((value+.055)/1.055,2.4);}
            return .2126*C(color.R)+.7152*C(color.G)+.0722*C(color.B);
        }
        return (Math.Max(L(one),L(two))+.05)/(Math.Min(L(one),L(two))+.05);
    }
    static Color Over(Color foreground,Color background)
    {
        double alpha=foreground.A/255.0;
        return Color.FromRgb((byte)Math.Round(foreground.R*alpha+background.R*(1-alpha)),(byte)Math.Round(foreground.G*alpha+background.G*(1-alpha)),(byte)Math.Round(foreground.B*alpha+background.B*(1-alpha)));
    }
    static IEnumerable<DependencyObject> Visuals(DependencyObject root)
    {
        yield return root;
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)
            foreach(var child in Visuals(VisualTreeHelper.GetChild(root,i)))yield return child;
    }

    internal static void Run(Assembly assembly)
    {
        Type application=assembly.GetType("TaskGlass.GlassApplication")!,preferences=assembly.GetType("TaskGlass.Preferences")!;
        // Skip application field initializers, named events, startup, engines and config loading.
        object app=RuntimeHelpers.GetUninitializedObject(application);
        var preferenceField=application.GetField("preferences",All)!;
        object Prefs(string tint)=>JsonSerializer.Deserialize(JsonSerializer.Serialize(new {TaskbarColor=tint,Opacity=41,Blur=0}),preferences)!;
        preferenceField.SetValue(app,Prefs("#FFFFFF"));
        application.GetField("<Exiting>k__BackingField",All)!.SetValue(app,true);
        var window=(Window)Activator.CreateInstance(assembly.GetType("TaskGlass.SettingsWindow")!,All&~BindingFlags.Static,null,new[]{app},null)!;
        var failures=new List<string>();
        void Contrast(string label,Color ink,Color background)
        {
            double ratio=Ratio(Over(ink,background),background);
            if(ratio<4.5)failures.Add($"{label}: {ink} over {background}, {ratio:0.00}:1");
        }
        try
        {
            var content=(FrameworkElement)window.Content;
            content.Measure(new Size(960,760));content.Arrange(new Rect(0,0,960,760));content.UpdateLayout();
            Color background=ColorOf(((Border)window.FindName("GlassTint")).Background);
            if(ColorOf(window.Foreground)!=Colors.Black)failures.Add("White settings material must use black foreground");
            foreach(string name in new[]{"OpacitySlider","RadiusSlider","BlurSlider"})
            {
                var slider=(Slider)window.FindName(name);
                var panel=(Panel)VisualTreeHelper.GetParent(slider);
                int index=panel.Children.IndexOf(slider);
                var value=((DockPanel)panel.Children[index-1]).Children.OfType<TextBlock>().First();
                Contrast(name+" value",ColorOf(value.Foreground),background);
            }
            var placeholder=Visuals(content).OfType<TextBlock>().Single(text=>text.Text=="⌕  搜索设置");
            var search=(TextBox)window.FindName("SettingsSearch");
            Contrast("Settings search placeholder",ColorOf(placeholder.Foreground),Over(ColorOf(search.Background),background));
            var page=(StackPanel)window.FindName("PageContent");
            var notices=page.Children.OfType<Border>().First(card=>Equals(card.Tag,"通知"));
            var source=(Expander)((StackPanel)notices.Child).Children[0];
            var title=((StackPanel)source.Header).Children.OfType<TextBlock>().First();
            Contrast("Notification source title",ColorOf(title.Foreground),background);
            Contrast("Notification summary",ColorOf(((TextBlock)window.FindName("NotificationSummaryText")).Foreground),background);
            var colorLabel=(TextBlock)window.FindName("TaskbarColorText");
            var labelInk=ColorOf(colorLabel.Foreground);labelInk.A=(byte)Math.Round(labelInk.A*colorLabel.Opacity);
            Contrast("Appearance color label with element opacity",labelInk,background);
            foreach(string name in new[]{"monitorChoice","performanceChoice"})
            {
                var combo=(ComboBox)window.GetType().GetField(name,All)!.GetValue(window)!;
                combo.ApplyTemplate();
                var popup=(Popup)combo.Template.FindName("PART_Popup",combo)!;
                Color sheet=ColorOf(((Border)popup.Child).Background);
                Contrast(name+" popup items",ColorOf((Brush)window.FindResource("SettingsTextBrush")),Over(sheet,background));
            }
            preferenceField.SetValue(app,Prefs("#242B2D"));
            window.GetType().GetMethod("ReadColors",All)!.Invoke(window,null);
            if(ColorOf(window.Foreground)!=Colors.White)failures.Add("Dark settings material must restore white foreground");
            foreach (string tint in new[]{"#242B2D","#FFFFFF"})
            foreach (double opacity in new[]{10d,41d,80d})
            {
                preferenceField.SetValue(app,JsonSerializer.Deserialize(JsonSerializer.Serialize(new{TaskbarColor=tint,Opacity=opacity,Blur=0}),preferences)!);
                window.GetType().GetMethod("ReadColors",All)!.Invoke(window,null);
                var material=TaskGlass.GlassTheme.Material(tint,opacity);
                var card=page.Children.OfType<Border>().First();
                var cardBackground=Over(ColorOf(card.Background),material.WorstBackground);
                Contrast($"{tint}/{opacity} card secondary text",ColorOf((Brush)window.FindResource("SettingsMutedBrush")),cardBackground);
                var button=new Button {Style=(Style)window.FindResource(typeof(Button))};
                button.Resources=window.Resources;
                Contrast($"{tint}/{opacity} button over card",ColorOf((Brush)window.FindResource("SettingsTextBrush")),Over(ColorOf(button.Background),cardBackground));
            }
            foreach (string tint in new[]{"#242B2D","#737373","#747474","#757575","#FFFFFF"})
            {
                var prefs=new TaskGlass.Preferences {TaskbarColor=tint,Opacity=10};
                var picker=new TaskGlass.ColorPickerWindow("#19A978","Synthetic scope",prefs);
                try
                {
                    var body=(FrameworkElement)picker.Content;
                    body.Measure(new Size(560,780));body.Arrange(new Rect(0,0,560,780));body.UpdateLayout();
                    foreach(string name in new[]{"HexInput","RedInput","GreenInput","BlueInput"})
                    {
                        var input=(TextBox)picker.FindName(name);
                        Contrast($"{tint} color picker {name}",ColorOf(input.Foreground),Over(ColorOf(input.Background),TaskGlass.GlassTheme.Material(tint,10).WorstBackground));
                    }
                    if(picker.IsVisible)failures.Add("Color picker contrast checks must stay hidden");
                }
                finally {picker.Close();}
            }
            if(window.IsVisible)failures.Add("Settings material checks must never show a window");
        }
        finally{window.Close();}
        if(failures.Count>0)throw new Exception("Settings material contrast failures:\n"+string.Join('\n',failures));
        Console.WriteLine("PASS: light settings slider values, search placeholder, notification title/summary and unopened ComboBox popup contrast; dark foreground restored. Synthetic preferences only; no window shown, config written or engine started.");
    }
}
