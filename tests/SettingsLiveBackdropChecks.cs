using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

internal static class SettingsLiveBackdropChecks
{
    const BindingFlags All=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    [StructLayout(LayoutKind.Sequential)]struct Message
    {
        internal nint Window; internal uint Id; internal nuint WParam; internal nint LParam;
        internal uint Time; internal int X,Y; internal uint Private;
    }
    [StructLayout(LayoutKind.Sequential)]struct NativeRect{internal int Left,Top,Right,Bottom;}
    [DllImport("user32.dll")]static extern bool PeekMessage(out Message message,nint window,uint first,uint last,uint remove);
    [DllImport("user32.dll")]static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")]static extern nint DispatchMessage(ref Message message);
    [DllImport("user32.dll")]static extern bool SetWindowPos(nint window,nint after,int x,int y,int width,int height,uint flags);
    [DllImport("user32.dll")]static extern bool GetWindowRect(nint window,out NativeRect rect);
    [DllImport("user32.dll")]static extern nint GetForegroundWindow();
    [DllImport("user32.dll",EntryPoint="SendMessageW")]static extern nint SendMessage(nint window,uint message,nint wp,nint lp);
    static void Check(bool value,string label){if(!value)throw new Exception(label);}
    static void Pump(int milliseconds,Action? step=null)
    {
        var elapsed=Stopwatch.StartNew();
        while(elapsed.ElapsedMilliseconds<milliseconds)
        {
            step?.Invoke();
            while(PeekMessage(out var message,0,0,0,1))
            {TranslateMessage(ref message);DispatchMessage(ref message);}
            Thread.Sleep(2);
        }
    }
    internal static void Run(Assembly assembly)
    {
        Type application=assembly.GetType("TaskGlass.GlassApplication")!,type=assembly.GetType("TaskGlass.SettingsWindow")!;
        // Bypass application construction so no config, engines, named events or external integrations start.
        object app=RuntimeHelpers.GetUninitializedObject(application);
        object preferences=JsonSerializer.Deserialize("{\"TaskbarColor\":\"#563875\",\"Opacity\":10,\"Blur\":11}",assembly.GetType("TaskGlass.Preferences")!)!;
        application.GetField("preferences",All)!.SetValue(app,preferences);
        application.GetField("<Exiting>k__BackingField",All)!.SetValue(app,true);
        var window=(Window)Activator.CreateInstance(type,All&~BindingFlags.Static,null,new[]{app},null)!;
        var image=(Image)window.FindName("SettingsBackdrop");
        var backdropField=type.GetField("backdropSource",All)!;
        var applyMaterial=type.GetMethod("ApplyMaterial",All)!;
        var pixel=BitmapSource.Create(1,1,96,96,PixelFormats.Bgra32,null,new byte[]{73,44,16,255},4);pixel.Freeze();
        var changes=new List<long>();var time=Stopwatch.StartNew();bool seeding=false;
        var descriptor=DependencyPropertyDescriptor.FromProperty(Image.SourceProperty,typeof(Image));
        EventHandler changed=(_,_)=>{if(!seeding&&!ReferenceEquals(image.Source,pixel))changes.Add(time.ElapsedMilliseconds);};
        void Seed()
        {
            seeding=true;
            typeof(TaskGlass.BackdropImage).GetField("frame",All)!.SetValue(image,new TaskGlass.BackdropFrame(pixel,new Rect(-32000,-32000,4000,4000)));
            backdropField.SetValue(window,pixel);applyMaterial.Invoke(window,null);seeding=false;
        }
        descriptor.AddValueChanged(image,changed);
        nint foreground=GetForegroundWindow();
        try
        {
            window.WindowStartupLocation=WindowStartupLocation.Manual;window.Left=-30000;window.Top=-30000;
            window.Width=320;window.Height=240;window.ShowActivated=false;window.ShowInTaskbar=false;
            window.IsHitTestVisible=false;window.Show();
            nint handle=new WindowInteropHelper(window).Handle;
            Check(GetWindowRect(handle,out var bounds)&&bounds.Right<SystemParameters.VirtualScreenLeft&&bounds.Bottom<SystemParameters.VirtualScreenTop,
                "Synthetic settings window must stay outside the entire visible desktop");
            Pump(230);Seed();changes.Clear();time.Restart();
            SendMessage(handle,0x0231,0,0);
            long nextMove=0;int moves=0,viewChanges=0;
            // Pump native messages rather than a DispatcherFrame, like the native sizing/moving modal loop.
            Pump(650,()=>
            {
                if(time.ElapsedMilliseconds<nextMove)return;
                nextMove=time.ElapsedMilliseconds+10;
                var previous=((TaskGlass.BackdropImage)image).Viewport;
                SetWindowPos(handle,0,-30000+(moves++%20),-30000,0,0,0x0015);
                if(previous!=((TaskGlass.BackdropImage)image).Viewport)viewChanges++;
            });
            Check(moves>=12,$"Continuous movement reproduction must generate frequent real window position changes (actual {moves}, refreshes {changes.Count})");
            Check(viewChanges>=12,$"Cached viewport must follow continuous movement before release (actual {viewChanges} updates)");
            Check(changes.Count==0&&ReferenceEquals(image.Source,pixel),"Dragging must keep the same screen texture instead of uploading new screenshots");
            int movingRefreshes=viewChanges;
            SendMessage(handle,0x0232,0,0);
            Pump(150);Seed();changes.Clear();Pump(200);
            Check(changes.Count==0,"An idle settings window must not keep sampling, even after an unavailable offscreen capture");
            SetWindowPos(handle,0,-29970,-30000,0,0,0x0015);window.Hide();
            Check(image.Source is null&&backdropField.GetValue(window) is null,"Hiding settings must release the sampled background");
            Seed();changes.Clear();Pump(140);
            Check(changes.Count==0,"Hiding settings must cancel pending captures");
            Check(!window.IsVisible,"Backdrop tests must leave the settings window hidden");
            Check(Math.Abs(((BlurEffect)((TaskGlass.BackdropImage)image).Pixels.Effect).Radius-6.6)<.000001,"Live refresh must preserve the latest saved blur strength");
            var tint=((TaskGlass.BackdropImage)image).MaterialLayer.Effect;
            var tintRg=(Point)tint.GetType().GetProperty("TintRG",All)!.GetValue(tint)!;
            var tintBo=(Point)tint.GetType().GetProperty("TintBO",All)!.GetValue(tint)!;
            var material=assembly.GetType("TaskGlass.GlassTheme")!.GetMethod("Material",All)!.Invoke(null,new object[]{"#563875",10.0})!;
            double expectedOpacity=(double)material.GetType().GetProperty("TintOpacity",All)!.GetValue(material)!;
            Check(Math.Abs(tintRg.X-86/255.0)<.000001&&Math.Abs(tintRg.Y-56/255.0)<.000001&&Math.Abs(tintBo.X-117/255.0)<.000001&&Math.Abs(tintBo.Y-expectedOpacity)<.000001,
                "Live refresh must preserve the latest saved tint and 10 percent opacity");
            for(DependencyObject? element=(TextBox)window.FindName("SettingsSearch");element is not null;element=VisualTreeHelper.GetParent(element))
                if(element is UIElement ui)Check(ui.Effect is not BlurEffect&&ui.Effect?.GetType().Name!="GlassTintEffect","Backdrop effects must not blur settings controls or text");
            window.Show();Seed();changes.Clear();window.Close();
            Check(image.Source is null&&backdropField.GetValue(window) is null,"Closing settings must release the sampled background");
            changes.Clear();Pump(140);
            Check(changes.Count==0,"Closing settings must cancel its timer without later capture work");
            Check(GetForegroundWindow()==foreground,"Offscreen backdrop refresh must preserve keyboard focus");
            Console.WriteLine($"PASS: settings viewport follows {movingRefreshes} moves with zero new textures; native loop, idle/hide/close, tint/opacity/blur/text/focus preserved. Synthetic scene only.");
        }
        finally{descriptor.RemoveValueChanged(image,changed);window.Close();}
    }
}
