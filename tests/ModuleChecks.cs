using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Runtime.InteropServices;
using Expression = System.Linq.Expressions.Expression;

internal static class ModuleChecks
{
    [StructLayout(LayoutKind.Sequential)] private struct WorkRect {public int Left,Top,Right,Bottom;}
    [DllImport("user32.dll")]private static extern bool SystemParametersInfo(uint action,uint parameter,out WorkRect rect,uint flags);
    internal static void Run(Assembly assembly)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        void Check(bool value, string label) { if (!value) throw new Exception(label); }
        Type T(string name) => assembly.GetType("TaskGlass." + name) ?? throw new Exception("Missing desktop module: " + name);
        Check(assembly.GetType("TaskGlass.GlassMotion") is not null, "Shared interruptible glass motion must exist");
        var preferences = T("Preferences");
        object Normalize(string json)
        {
            var value = JsonSerializer.Deserialize(json, preferences)!;
            return preferences.GetMethod("Normalize", flags)!.Invoke(null, new[] { value })!;
        }
        object P(object value, string name) => preferences.GetProperty(name, flags)!.GetValue(value)!;
        var legacy = Normalize("{\"TaskbarColor\":\"#563875\",\"Blur\":59}");
        Check((bool)P(legacy, "AnimationEnabled") && !(bool)P(legacy, "LauncherFullscreen"), "Legacy settings retain enabled motion and compact launcher defaults");
        Check((string)P(legacy, "TaskbarColor") == "#563875" && (double)P(legacy, "Blur") == 59, "Legacy tint and blur are not reset by module migration");
        var invalid = Normalize("{\"Blur\":300,\"Opacity\":-5,\"Radius\":200,\"TaskbarColor\":\"invalid\",\"PerformancePreset\":\"bad\",\"StatusComponents\":[\"Clock\",\"Clock\",\"Invalid\"]}");
        Check((double)P(invalid, "Blur") == 100 && (double)P(invalid, "Opacity") == 10 && (double)P(invalid, "Radius") == 24, "Imported settings clamp real preference ranges");
        Check((string)P(invalid, "PerformancePreset") == "均衡", "Unknown performance profiles normalize safely");
        var components = (string[])P(invalid, "StatusComponents");
        Check(components.SequenceEqual(new[] { "Clock" }), "Component order removes duplicates and unknown keys");
        var motion = T("GlassMotion");
        motion.GetProperty("Enabled", flags)!.SetValue(null, false);
        var border = new Border { Opacity = .2 };
        motion.GetMethod("To", flags)!.Invoke(null, new object[] { border, UIElement.OpacityProperty, 1.0, 120 });
        Check(border.Opacity == 1 && !border.HasAnimatedProperties, "Disabled animation immediately reaches final state without a clock");
        var hotkey = T("LauncherHotkey");
        Check((bool)hotkey.GetMethod("IsValid", flags)!.Invoke(null, new object[] { "Ctrl+Alt+Space" })!, "Launcher shortcut accepts the approved default");
        Check(!(bool)hotkey.GetMethod("IsValid", flags)!.Invoke(null, new object[] { "Win" })!, "Bare Windows key is not hijacked");
        Console.WriteLine("PASS: module preferences preserve legacy appearance, validate component order and ranges; disabled motion has no animation clocks; shortcut validation keeps native Windows key.");
    }
    internal static void Live(Assembly assembly)
    {
        const BindingFlags flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
        Type T(string n)=>assembly.GetType("TaskGlass."+n)!;
        object? Call(object target,string method,params object?[] args)=>target.GetType().GetMethod(method,flags)!.Invoke(target,args);
        void Check(bool pass,string label){if(!pass)throw new Exception(label);}
        void Pump(int ms)
        {var frame=new DispatcherFrame();var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(ms)};timer.Tick+=(_,_)=>{timer.Stop();frame.Continue=false;};timer.Start();Dispatcher.PushFrame(frame);}
        T("GlassMotion").GetMethod("Configure",flags)!.Invoke(null,new object[]{true});
        var prefs=JsonSerializer.Deserialize("{\"TaskbarColor\":\"#FFFFFF\",\"Blur\":0}",T("Preferences"))!;
        var panel=(Window)Activator.CreateInstance(T("GlassPanel"),flags&~BindingFlags.Static,null,new object[]{"TaskGlass · 动效验证",460.0,300.0},null)!;
        try
        {
            var content=(Grid)T("GlassPanel").GetField("ContentHost",flags)!.GetValue(panel)!;
            var action=new Button{Content="保存",Style=(Style)panel.FindResource("SettingsPrimaryButton"),Width=160,Height=48};content.Children.Add(action);
            Call(panel,"ApplyAppearance",prefs);Call(panel,"Present",false);
            for(int attempt=0;attempt<30&&Math.Abs(panel.Opacity-1)>.001;attempt++)Pump(50);
            Check(panel.IsVisible&&Math.Abs(panel.Opacity-1)<.001,$"Panel finishes its opening animation visible: visible={panel.IsVisible}, opacity={panel.Opacity}");
            var ink=((SolidColorBrush)action.Foreground).Color;var background=((SolidColorBrush)action.Background).Color;
            double Light(Color c){double F(byte v){double x=v/255.0;return x<=.04045?x/12.92:Math.Pow((x+.055)/1.055,2.4);}return .2126*F(c.R)+.7152*F(c.G)+.0722*F(c.B);}
            Check((Math.Max(Light(ink),Light(background))+.05)/(Math.Min(Light(ink),Light(background))+.05)>=4.5,"Light panel primary action retains readable text");
            Call(panel,"Dismiss");Pump(35);Call(panel,"Present",false);Pump(230);
            Check(panel.IsVisible&&panel.IsHitTestVisible&&!(bool)T("GlassPanel").GetProperty("IsClosing",flags)!.GetValue(panel)!,"Open-close-open preserves the last reopen request");
            Call(panel,"Dismiss");Pump(210);Check(!panel.IsVisible,"Closed panel leaves no transparent hit-testing window");
            T("GlassMotion").GetMethod("Configure",flags)!.Invoke(null,new object[]{false});Call(panel,"Present",false);Call(panel,"Dismiss");Check(!panel.IsVisible,"Disabled motion closes synchronously");
        }
        finally{panel.Close();}
        var audio=T("SystemControls").GetMethod("ReadAudio",flags)!.Invoke(null,null)!;
        if((bool)audio.GetType().GetProperty("Available")!.GetValue(audio)!)
            Check((double)audio.GetType().GetProperty("Volume")!.GetValue(audio)! is >=0 and <=100,"Real endpoint volume is in range");
        var monitor=Activator.CreateInstance(T("ResourceMonitor"),flags&~BindingFlags.Static,null,Array.Empty<object>(),null)!;
        try{Call(monitor,"Read");Pump(1100);var sample=Call(monitor,"Read")!;var cpu=(double?)sample.GetType().GetProperty("Cpu")!.GetValue(sample);Check(cpu is >=0 and <=100,"Real CPU sample is valid after warmup");}
        finally{((IDisposable)monitor).Dispose();}
        var defaults=T("LauncherData").GetMethod("CreateDefaults",flags)!.Invoke(null,null)!;string launcher=(string)Call(defaults,"ToJson")!;
        string serialized=(string)T("SettingsBackup").GetMethod("Serialize",flags)!.Invoke(null,new[]{prefs,(object)launcher})!;
        Check(T("SettingsBackup").GetMethod("Parse",flags)!.Invoke(null,new object[]{serialized}) is not null,"Validated settings and launcher backup round-trip");
        SystemParametersInfo(0x30,0,out var originalWork,0);
        var getter=Expression.Lambda(typeof(Func<>).MakeGenericType(T("Preferences")),Expression.Constant(prefs)).Compile();
        var ctor=T("StatusBarController").GetConstructors(flags&~BindingFlags.Static).Single();
        Type screenAction=ctor.GetParameters()[2].ParameterType;
        var screenParameter=Expression.Parameter(screenAction.GenericTypeArguments[0]);
        var screenCallback=Expression.Lambda(screenAction,Expression.Empty(),screenParameter).Compile();
        var bar=ctor.Invoke(new object[]{getter,(Action)(()=>{}),screenCallback,(Action)(()=>{}),(Func<int>)(()=>0),(Action)(()=>{})});
        try
        {
            Call(bar,"Update");Pump(450);SystemParametersInfo(0x30,0,out var reserved,0);
            Check((int)T("StatusBarController").GetProperty("Count",flags)!.GetValue(bar)! == 1,"One primary-screen status bar is created");
            Check(reserved.Top>originalWork.Top&&reserved.Bottom==originalWork.Bottom,"Top AppBar reserves its own space while preserving bottom workarea");
        }
        finally{((IDisposable)bar).Dispose();Pump(450);}
        SystemParametersInfo(0x30,0,out var restored,0);
        Check(restored.Top==originalWork.Top&&restored.Bottom==originalWork.Bottom,"Disposing status bar restores original workarea");
        Console.WriteLine("PASS: live panel interruption, actual hide, disabled motion, light-theme action contrast, real audio/CPU read and backup round-trip. No system values changed.");
        Console.WriteLine("PASS: native AppBar reserves top space, preserves native bottom taskbar area and restores workarea on disposal.");
    }
    internal static void Render(Assembly assembly)
    {
        const BindingFlags flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
        Type T(string name)=>assembly.GetType("TaskGlass."+name)!;
        var stub=System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(T("GlassApplication"));
        var p=JsonSerializer.Deserialize("{\"TaskbarColor\":\"#242B2D\",\"Blur\":59}",T("Preferences"))!;
        T("GlassApplication").GetField("preferences",flags)!.SetValue(stub,p);
        T("GlassApplication").GetField("<State>k__BackingField",flags)!.SetValue(stub,"界面检查 · 未启动采集");
        var window=(Window)Activator.CreateInstance(T("SettingsWindow"),flags&~BindingFlags.Static,null,new[]{stub},null)!;
        try
        {
            var content=(FrameworkElement)window.Content;
            content.Measure(new Size(960,760));content.Arrange(new Rect(0,0,960,760));content.UpdateLayout();
            var bitmap=new RenderTargetBitmap(1440,1140,144,144,PixelFormats.Pbgra32);bitmap.Render(content);
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
            System.IO.Directory.CreateDirectory("D:\\codex\\QuickFileDesk\\artifacts\\unified-checks");
            using var file=System.IO.File.Create("D:\\codex\\QuickFileDesk\\artifacts\\unified-checks\\settings.png");encoder.Save(file);
        }
        finally{T("GlassApplication").GetField("<Exiting>k__BackingField",flags)!.SetValue(stub,true);window.Close();}
        Console.WriteLine("PASS: rendered actual settings layout without collectors or saved-setting changes.");
    }
}
