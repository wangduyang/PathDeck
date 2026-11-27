using System.Reflection;
using Expression = System.Linq.Expressions.Expression;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

internal static class GlassShaderChecks
{
    const BindingFlags All=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    static void Check(bool value,string label){if(!value)throw new Exception(label);}
    static object Property(object value,string name)=>value.GetType().GetProperty(name,All)!.GetValue(value)!;
    static Effect Effect(Assembly assembly,object material)
    {
        var effect=(Effect)Activator.CreateInstance(assembly.GetType("TaskGlass.GlassTintEffect")!,true)!;
        effect.GetType().GetMethod("Apply",All)!.Invoke(effect,new[]{material});
        return effect;
    }
    static BitmapSource Render(FrameworkElement element,int width,int height)
    {
        element.Width=width;element.Height=height;
        element.Measure(new Size(width,height));element.Arrange(new Rect(0,0,width,height));element.UpdateLayout();
        var image=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);image.Render(element);image.Freeze();return image;
    }
    static byte[] Pixel(BitmapSource bitmap,int x,int y)
    {
        var pixel=new byte[4];bitmap.CopyPixels(new Int32Rect(x,y,1,1),pixel,4,0);return pixel;
    }
    static Color Straight(byte[] pixel)=>pixel[3]==0?Colors.Transparent:Color.FromArgb(pixel[3],(byte)Math.Min(255,Math.Round(pixel[2]*255.0/pixel[3])),(byte)Math.Min(255,Math.Round(pixel[1]*255.0/pixel[3])),(byte)Math.Min(255,Math.Round(pixel[0]*255.0/pixel[3])));
    static Color Expected(object material,Color source)
    {
        Color tint=(Color)Property(material,"Tint");
        double alpha=(double)Property(material,"TintOpacity"),lum=(double)Property(material,"LuminosityOpacity");
        double y=(.2126*source.R+.7152*source.G+.0722*source.B)/255.0;
        double target=(.2126*tint.R+.7152*tint.G+.0722*tint.B)/255.0;
        static byte Channel(double value)=>(byte)Math.Round(Math.Clamp(value,0,1)*255);
        double Finish(byte channel,byte tintChannel)=>((channel/255.0-y)*1.18+y+(target-y)*lum)*(1-alpha)+tintChannel/255.0*alpha;
        return Color.FromArgb(source.A,Channel(Finish(source.R,tint.R)),Channel(Finish(source.G,tint.G)),Channel(Finish(source.B,tint.B)));
    }
    static int Error(byte[] actual,Color expected)
    {
        double alpha=expected.A/255.0;
        return new[]{Math.Abs(actual[0]-(int)Math.Round(expected.B*alpha)),Math.Abs(actual[1]-(int)Math.Round(expected.G*alpha)),Math.Abs(actual[2]-(int)Math.Round(expected.R*alpha)),Math.Abs(actual[3]-expected.A)}.Max();
    }
    static BitmapSource Pattern()
    {
        var pixels=new byte[96*64*4];
        var colors=new[]{Colors.Red,Colors.Blue,Colors.Yellow,Colors.Cyan,Colors.Magenta,Colors.White};
        for(int y=0;y<64;y++)for(int x=0;x<96;x++)
        {
            var color=colors[(x/12+y/16)%colors.Length];int i=(y*96+x)*4;
            pixels[i]=color.B;pixels[i+1]=color.G;pixels[i+2]=color.R;pixels[i+3]=255;
        }
        var image=BitmapSource.Create(96,64,96,96,PixelFormats.Bgra32,null,pixels,96*4);image.Freeze();return image;
    }
    static Border Background(BitmapSource source,Effect? outer,Effect? inner)=>new()
    {Effect=outer,Child=new Image{Source=source,Stretch=Stretch.Fill,Effect=inner}};

    static void AdditionalSurfaces(Assembly assembly)
    {
        Type preferences=assembly.GetType("TaskGlass.Preferences")!;
        object Prefs(string tint)=>JsonSerializer.Deserialize(JsonSerializer.Serialize(new {TaskbarColor=tint,Opacity=41,Blur=0,StatusComponents=Array.Empty<string>()}),preferences)!;
        object light=Prefs("#FFFFFF"),dark=Prefs("#242B2D");
        var picker=(Window)Activator.CreateInstance(assembly.GetType("TaskGlass.ColorPickerWindow")!,All&~BindingFlags.Static,null,new[]{(object)"#315E82","材质模拟检查",light},null)!;
        try
        {
            Render((FrameworkElement)picker.Content,560,780);
            var source=(Image)picker.FindName("Backdrop");var layer=(Border)picker.FindName("BackdropMaterial");
            var field=(FrameworkElement)picker.FindName("ColorField");var swatch=(Border)picker.FindName("NewSwatch");
            var raw=Pattern();source.Source=raw;picker.GetType().GetMethod("ApplyMaterial",All)!.Invoke(picker,new[]{light});
            var cached=(TaskGlass.BackdropImage)source;
            Check(cached.MaterialLayer.Effect?.GetType().Name=="GlassTintEffect"&&cached.Pixels.Effect is BlurEffect{Radius:0}&&cached.MaterialLayer.CacheMode is BitmapCache,"Color picker caches post-blur glass and retains zero-blur clear glass");
            Check(((SolidColorBrush)((Border)picker.FindName("Tint")).Background).Color.A==0,"Color picker must not cover its sampled glass with a thick tint");
            Check(((SolidColorBrush)((TextBox)picker.FindName("HexInput")).Foreground).Color==Colors.Black,"Light color picker inputs use the dynamic dark foreground");
            for(DependencyObject? parent=field;parent is not null;parent=VisualTreeHelper.GetParent(parent))
                if(parent is UIElement visual)Check(visual.Effect?.GetType().Name!="GlassTintEffect"&&visual.Effect is not BlurEffect,"Color picker HSV field must stay outside all background effects");
            Check(((SolidColorBrush)swatch.Background).Color==Color.FromRgb(49,94,130),"Color picker selected-color preview must not be tinted by the window material");
            picker.GetType().GetMethod("ApplyMaterial",All)!.Invoke(picker,new[]{dark});
            Check(((SolidColorBrush)((TextBox)picker.FindName("HexInput")).Foreground).Color==Colors.White,"Dark color picker inputs update their dynamic foreground");
            Check(ReferenceEquals(source.Source,raw)&&((SolidColorBrush)swatch.Background).Color==Color.FromRgb(49,94,130),"Material changes preserve the raw sample and selected preview color");
        }
        finally{picker.Close();}
        Check(((Image)picker.FindName("Backdrop")).Source is null,"Closing color picker releases its background sample");

        Type controller=assembly.GetType("TaskGlass.StatusBarController")!;
        var ctor=controller.GetConstructors(All&~BindingFlags.Static).Single();
        var getter=Expression.Lambda(typeof(Func<>).MakeGenericType(preferences),Expression.Constant(light,preferences)).Compile();
        Type screenAction=ctor.GetParameters()[2].ParameterType,screenType=screenAction.GenericTypeArguments[0];
        var callback=Expression.Lambda(screenAction,Expression.Empty(),Expression.Parameter(screenType)).Compile();
        var owner=(IDisposable)ctor.Invoke(new object[]{getter,(Action)(()=>{}),callback,(Action)(()=>{}),(Func<int>)(()=>0),(Action)(()=>{})});
        var topType=controller.GetNestedType("TopBar",BindingFlags.NonPublic)!;
        var top=(Window)Activator.CreateInstance(topType,All&~BindingFlags.Static,null,new[]{owner,screenType.GetProperty("PrimaryScreen")!.GetValue(null)!},null)!;
        try
        {
            topType.GetMethod("Apply",All)!.Invoke(top,new[]{light});
            var sample=(Image)topType.GetField("sample",All)!.GetValue(top)!;
            sample.Source=Pattern();topType.GetMethod("ApplyMaterial",All)!.Invoke(top,new[]{light});
            var material=(Border)topType.GetField("backdropLayer",All)!.GetValue(top)!;
            var glass=(Border)topType.GetField("glass",All)!.GetValue(top)!;
            Check(material.Effect?.GetType().Name=="GlassTintEffect"&&sample.Effect is BlurEffect{Radius:0},"Top status bar shares the post-blur shader and retains clear glass at blur zero");
            Check(sample.Source is not null&&((SolidColorBrush)glass.Background).Color.A==0,"Top status bar preserves the raw sample without a second opaque layer, including when fullscreen policy hides it");
            Check(top.Height==42&&!(bool)topType.GetField("registered",All)!.GetValue(top)!,"Material-only status bar test preserves 42 DIP height and never registers a real AppBar");
            Check(ReferenceEquals(material.Child,sample)&&glass.Effect is null,"Top status bar foreground controls stay outside the background effects");
        }
        finally{top.Close();owner.Dispose();}
        Check(((Image)topType.GetField("sample",All)!.GetValue(top)!).Source is null,"Closing top status bar releases its background sample");
    }

    internal static void Run(Assembly assembly)
    {
        var theme=assembly.GetType("TaskGlass.GlassTheme")!;
        object Material(string tint)=>(object)theme.GetMethod("Material",All)!.Invoke(null,new object[]{tint,41.0})!;
        foreach(string tint in new[]{"#242B2D","#FFFFFF","#563875"})
        {
            var material=Material(tint);
            foreach(var source in new[]{Colors.Red,Colors.Blue,Colors.Cyan,Colors.White,Colors.Black,Color.FromRgb(122,87,201),Color.FromArgb(128,255,0,0),Color.FromArgb(64,0,255,0),Colors.Transparent})
            {
                var layer=new Border{Effect=Effect(assembly,material),Background=new SolidColorBrush(source)};
                var rendered=Render(layer,32,32);var actual=Pixel(rendered,16,16);
                Check(Error(actual,Expected(material,source))<=3,$"Glass shader literal sRGB, tint {tint}, source {source}, actual {string.Join(',',actual)}");
                Check(Math.Abs(actual[3]-source.A)<=1,"Glass shader must retain source alpha, including fully transparent pixels");
                var clone=(Effect)layer.Effect.Clone();Check(clone.GetType()==layer.Effect.GetType(),"Glass shader must support WPF Freezable cloning");
            }
        }
        var dark=Material("#242B2D");var pattern=Pattern();
        var blurred=Render(Background(pattern,null,new BlurEffect{Radius=8}),96,64);
        var correct=Render(Background(pattern,Effect(assembly,dark),new BlurEffect{Radius=8}),96,64);
        var wrong=Render(Background(pattern,new BlurEffect{Radius=8},Effect(assembly,dark)),96,64);
        int difference=0;
        for(int y=12;y<52;y+=4)for(int x=12;x<84;x+=4)
        {
            var expected=Expected(dark,Straight(Pixel(blurred,x,y)));var actual=Pixel(correct,x,y);
            Check(Error(actual,expected)<=4,$"Glass must tone the already-blurred background at {x},{y}");
            var early=Pixel(wrong,x,y);difference=Math.Max(difference,actual.Zip(early,(a,b)=>Math.Abs(a-b)).Max());
        }
        Check(difference>=6,"The saturated test pattern must distinguish correct post-blur tint from premature clamping");
        var root=new Grid();root.Children.Add(Background(pattern,Effect(assembly,dark),new BlurEffect{Radius=8}));
        root.Children.Add(new Border{Width=20,Height=20,Background=new SolidColorBrush(Color.FromRgb(240,106,45)),HorizontalAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Top,Margin=new Thickness(30,20,0,0)});
        var foreground=Pixel(Render(root,96,64),40,30);
        Check(foreground.SequenceEqual(new byte[]{45,106,240,255}),"Foreground content must remain outside the background blur and tint shaders");
        AdditionalSurfaces(assembly);
        Console.WriteLine("PASS: WPF glass shader literal sRGB constants; premultiplied alpha; transparent pixels; Freezable clone; blur-before-tone order; foreground unaffected.");
        Console.WriteLine("PASS: color picker and 42-DIP top status bar share post-blur glass; zero-blur samples retained; preview colors and foreground controls unaffected; light/dark input text; closed samples released. No windows shown or AppBar registered.");
    }
}
