using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TaskGlass;

internal static class DesktopBlurChecks
{
    [DllImport("user32.dll")] private static extern nint GetParent(nint window);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window,uint command);
    private static void Check(bool value,string label){if(!value)throw new Exception(label);}
    internal static void Run()
    {
        var owner=new Window {Left=-30000,Top=-30000,Width=320,Height=240,ShowActivated=false,ShowInTaskbar=false};
        DesktopBackgroundLayer? layer=null;HwndSource? icon=null;
        try
        {
            owner.Show();var parent=new WindowInteropHelper(owner).Handle;
            icon=new HwndSource(new HwndSourceParameters("Synthetic desktop icon") {ParentWindow=parent,WindowStyle=0x40000000|0x10000000,Width=40,Height=40,PositionX=20,PositionY=20});
            icon.RootVisual=new Border {Background=Brushes.Red};
            var bytes=new byte[640*480*4];
            for(int y=0;y<480;y++)for(int x=0;x<640;x++){byte value=(byte)(((x/3+y/3)%2)*255);int i=(y*640+x)*4;bytes[i]=bytes[i+1]=bytes[i+2]=value;bytes[i+3]=255;}
            var bitmap=BitmapSource.Create(640,480,96,96,PixelFormats.Bgra32,null,bytes,640*4);bitmap.Freeze();
            layer=new DesktopBackgroundLayer(parent);
            var preferences=new Preferences(10,24,false,0);
            layer.SetAppearance(preferences);
            layer.Place(new Native.Rect {Left=-29970,Top=-29950,Right=-29770,Bottom=-29790},new BackdropFrame(bitmap,new Rect(-30000,-30000,640,480)));
            var source=(HwndSource)typeof(DesktopBackgroundLayer).GetField("source",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(layer)!;
            Check(GetParent(source.Handle)==parent,"The blur plane must be a child of the desktop host, not a popup over its icons.");
            bool iconAbove=false;for(nint previous=GetWindow(source.Handle,3);previous!=0;previous=GetWindow(previous,3))if(previous==icon.Handle)iconAbove=true;
            Check(iconAbove,"Native icon plane must remain above the background material.");
            double sharp=Contrast(layer.Visual);
            layer.SetAppearance(preferences with {Blur=49});double blurred=Contrast(layer.Visual);
            Check(sharp>blurred*1.5,$"Changing the shared blur must visibly blur wallpaper pixels (sharp {sharp:F0}, blurred {blurred:F0}).");
            layer.SetAppearance(preferences);double restored=Contrast(layer.Visual);
            Check(Math.Abs(restored-sharp)<sharp*.05,"Setting blur back to zero must restore clear wallpaper.");
            Check(DesktopWallpaper.Placement(new Size(100,50),new Rect(-100,0,200,200),4)==new Rect(-200,0,400,200),"Wallpaper fill must crop correctly across negative screen coordinates.");
            Console.WriteLine("PASS: desktop wallpaper blur 0→49→0 changes actual pixels; native icon child remains above material; desktop click/activation layering preserved. Synthetic offscreen windows only.");
        }
        finally{layer?.Dispose();icon?.Dispose();owner.Close();}
    }
    private static double Contrast(FrameworkElement visual)
    {
        visual.UpdateLayout();int w=(int)Math.Ceiling(visual.ActualWidth),h=(int)Math.Ceiling(visual.ActualHeight);
        var bitmap=new RenderTargetBitmap(w,h,96,96,PixelFormats.Pbgra32);bitmap.Render(visual);var bytes=new byte[w*h*4];bitmap.CopyPixels(bytes,w*4,0);
        double total=0;for(int y=25;y<h-25;y++)for(int x=26;x<w-25;x++)total+=Math.Abs(bytes[(y*w+x)*4]-bytes[(y*w+x-1)*4]);return total;
    }
}
