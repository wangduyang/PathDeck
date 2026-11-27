using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class GlassChecks
{
    const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static Assembly assembly=null!;
    static Type Type(string name)=>assembly.GetType("TaskGlass."+name)!;
    static object Make(string name,params object?[] args)=>Activator.CreateInstance(Type(name),All&~BindingFlags.Static,null,args,null)!;
    static object? Call(object item,string name,params object?[] args)=>item.GetType().GetMethod(name,All)!.Invoke(item,args);
    static object Get(object item,string name)=>item.GetType().GetProperty(name,All)!.GetValue(item)!;
    static void Check(bool value,string message){if(!value)throw new Exception(message);}
    static string Output=>@"D:\codex\QuickFileDesk\artifacts\unified-checks";

    internal static void Run(Assembly source)
    {
        assembly=source;
        System.IO.Directory.CreateDirectory(Output);
        var materialMethod=Type("GlassTheme").GetMethod("Material",All);
        Render(materialMethod is null?"glass-before.png":"glass-after.png");
        Check(materialMethod is not null,"Glass surfaces must share opacity and backdrop-color parameters instead of the fixed opaque tint");
        foreach(string tint in new[]{"#242B2D","#FFFFFF","#563875","#777777","#FF0000","#00FF00","#0000FF"})
            foreach(double opacity in new[]{10.0,41.0,80.0})
            {
                object material=materialMethod!.Invoke(null,new object[]{tint,opacity})!;
                var foreground=(Color)Get(material,"Foreground");
                double alpha=(double)Get(material,"TintOpacity");
                Check(alpha>=opacity/100&&alpha<=1,"Material must respect requested opacity and limit readability fallback to valid alpha");
                if(tint=="#242B2D"||tint=="#FFFFFF")Check(alpha<.82,"Neutral glass must not retain the previous 82% opaque floor");
                for(int r=0;r<=255;r+=51)for(int g=0;g<=255;g+=51)for(int b=0;b<=255;b+=51)
                {
                    Color composite=(Color)Call(material,"Composite",Color.FromRgb((byte)r,(byte)g,(byte)b))!;
                    Check(Ratio(foreground,composite)>=4.49,$"Glass foreground contrast for {tint}, opacity {opacity}");
                }
            }
        var dark=materialMethod!.Invoke(null,new object[]{"#242B2D",41.0})!;
        Color warm=(Color)Call(dark,"Composite",Color.FromRgb(225,90,45))!;
        Color cool=(Color)Call(dark,"Composite",Color.FromRgb(45,90,225))!;
        Check(warm.R>warm.B&&cool.B>cool.R,"Backdrop toning must preserve warm and cool hue separation");
        Console.WriteLine("PASS: shared glass opacity; readable tinted backdrops over an RGB grid; neutral surfaces below the old 82% floor; preserved warm/cool background hues. Synthetic data only.");
    }
    static double Ratio(Color one,Color two)
    {
        static double Lum(Color color){static double C(byte n){double s=n/255.0;return s<=.04045?s/12.92:Math.Pow((s+.055)/1.055,2.4);}return .2126*C(color.R)+.7152*C(color.G)+.0722*C(color.B);}
        return (Math.Max(Lum(one),Lum(two))+.05)/(Math.Min(Lum(one),Lum(two))+.05);
    }
    static BitmapSource Scene()
    {
        var visual=new DrawingVisual();
        using(var dc=visual.RenderOpen())
        {
            dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(30,64,91),Color.FromRgb(171,71,38),25),null,new Rect(0,0,1120,760));
            foreach(var item in new[]{(160.0,260.0,Colors.Coral),(510.0,95.0,Colors.Turquoise),(880.0,390.0,Colors.Gold),(750.0,650.0,Colors.MediumSlateBlue)})
                dc.DrawEllipse(new RadialGradientBrush(item.Item3,Colors.Transparent),null,new Point(item.Item1,item.Item2),280,290);
            for(int x=0;x<1120;x+=40)dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(50,255,255,255)),1),new Point(x,0),new Point(x,760));
            for(int y=0;y<760;y+=40)dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(50,255,255,255)),1),new Point(0,y),new Point(1120,y));
        }
        var image=new RenderTargetBitmap(1120,760,96,96,PixelFormats.Pbgra32);image.Render(visual);image.Freeze();return image;
    }
    static void Render(string file)
    {
        var scene=Scene();var captures=new List<(BitmapSource Image,Rect Rect)>();
        foreach(var (x,blur) in new[]{(42,100.0),(596,0.0)})
        {
            var panel=(Window)Make("GlassPanel",blur>0?"柔和磨砂 · 100%":"清晰玻璃 · 0%",482.0,430.0);
            try
            {
                var appearance=System.Text.Json.JsonSerializer.Deserialize("{\"TaskbarColor\":\"#242B2D\",\"Opacity\":41,\"Blur\":"+blur.ToString(System.Globalization.CultureInfo.InvariantCulture)+"}",Type("Preferences"))!;
                Call(panel,"ApplyAppearance",appearance);
                var host=(Grid)Type("GlassPanel").GetField("ContentHost",All)!.GetValue(panel)!;
                var body=new StackPanel {Margin=new Thickness(2,18,2,0)};
                body.Children.Add(new TextBlock {Text="控制中心",FontSize=29,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,0,0,10)});
                body.Children.Add(new TextBlock {Text="背景透色，文字与图标保持清晰",FontSize=15,Margin=new Thickness(0,0,0,28)});
                body.Children.Add(new Button {Content="无线网络     已连接",HorizontalContentAlignment=HorizontalAlignment.Left,Margin=new Thickness(0,0,0,12)});
                body.Children.Add(new Button {Content="蓝牙          已打开",HorizontalContentAlignment=HorizontalAlignment.Left,Margin=new Thickness(0,0,0,24)});
                body.Children.Add(new TextBlock {Text="亮度",FontSize=14});body.Children.Add(new Slider {Minimum=0,Maximum=100,Value=68,Margin=new Thickness(0,10,0,0)});
                host.Children.Add(body);
                var backdrop=new CroppedBitmap(scene,new Int32Rect(x,68,482,430));backdrop.Freeze();
                var set=Type("GlassPanel").GetMethod("SetBackdrop",All);
                if(set is not null)set.Invoke(panel,new object[]{backdrop});
                else {((Image)Type("GlassPanel").GetField("sample",All)!.GetValue(panel)!).Source=backdrop;Call(panel,"SetTint");}
                captures.Add((Capture(panel,482,430),new Rect(x,68,482,430)));
            }
            finally{panel.Close();}
        }
        var visual=new DrawingVisual();using(var dc=visual.RenderOpen())
        {
            dc.DrawImage(scene,new Rect(0,0,1120,760));
            foreach(var item in captures)dc.DrawImage(item.Image,item.Rect);
            dc.DrawText(new FormattedText("TaskGlass · 同一背景 / 41% 底色 / 仅背景参与材质处理",System.Globalization.CultureInfo.GetCultureInfo("zh-CN"),FlowDirection.LeftToRight,new Typeface("Microsoft YaHei UI"),20,Brushes.White,1),new Point(58,555));
            dc.DrawText(new FormattedText("合成测试背景 · 实际 WPF 面板渲染",System.Globalization.CultureInfo.GetCultureInfo("zh-CN"),FlowDirection.LeftToRight,new Typeface("Microsoft YaHei UI"),14,Brushes.White,1),new Point(58,592));
        }
        var result=new RenderTargetBitmap(1120,760,96,96,PixelFormats.Pbgra32);result.Render(visual);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(result));using var stream=System.IO.File.Create(System.IO.Path.Combine(Output,file));encoder.Save(stream);
    }
    static BitmapSource Capture(Window window,int width,int height)
    {
        var content=(FrameworkElement)window.Content;content.Measure(new Size(width,height));content.Arrange(new Rect(0,0,width,height));content.UpdateLayout();
        var image=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);image.Render(content);image.Freeze();return image;
    }
}
