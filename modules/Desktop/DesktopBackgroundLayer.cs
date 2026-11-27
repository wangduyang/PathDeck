using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace TaskGlass;

// A separate child surface below SHELLDLL_DefView. Icons remain native, sharp and interactive.
internal sealed class DesktopBackgroundLayer : IDisposable
{
    private readonly nint owner;
    private HwndSource? source;
    private readonly ImageBrush image = new() { Stretch=Stretch.Fill, ViewboxUnits=BrushMappingMode.RelativeToBoundingBox };
    private readonly BlurEffect blur = new() { RenderingBias=RenderingBias.Performance };
    private readonly GlassTintEffect tint = new();
    private readonly Border picture;
    private readonly Border material;
    private readonly Grid root = new();
    private double radius = 24;
    private double visibleHeight=double.PositiveInfinity;
    internal bool Live => source is not null && !source.IsDisposed && Native.IsWindow(source.Handle);

    internal DesktopBackgroundLayer(nint owner)
    {
        this.owner=owner;
        picture=new Border { Background=image,Effect=blur };
        material=new Border {Child=picture,Effect=tint,CacheMode=new BitmapCache()};
        root.Children.Add(material);
    }
    internal FrameworkElement Visual => root;
    internal void SetAppearance(Preferences value)
    {
        radius=value.Radius;blur.Radius=value.Blur*.6;tint.Apply(GlassTheme.Material(value.TaskbarColor,value.Opacity));
        UpdateClip();
    }

    internal void SetVisibleHeight(double pixels){visibleHeight=pixels;UpdateClip();}
    private void UpdateClip()
    {
        if(root.Width<=0||root.Height<=0||double.IsNaN(root.Width)||double.IsNaN(root.Height))return;
        double height=double.IsPositiveInfinity(visibleHeight)?root.Height:visibleHeight*(source?.CompositionTarget.TransformFromDevice.M22??1);
        root.Clip=new RectangleGeometry(new Rect(0,0,root.Width,Math.Clamp(height,0,root.Height)),radius,radius);
    }

    internal void Place(Native.Rect bounds,BackdropFrame wallpaper)
    {
        if(owner==0 || !Native.IsWindow(owner))return;
        var previous=SetThreadDpiAwarenessContext(GetWindowDpiAwarenessContext(owner));
        try
        {
            if(source is null)
            {
                var parameters=new HwndSourceParameters("PathDeck desktop material")
                {ParentWindow=owner,WindowStyle=0x40000000|0x10000000,ExtendedWindowStyle=0x08000020,UsesPerPixelOpacity=true,Width=1,Height=1};
                source=new HwndSource(parameters);source.CompositionTarget.BackgroundColor=Colors.Transparent;source.RootVisual=root;
                source.AddHook((nint hwnd,int message,nint w,nint l,ref bool handled)=>
                { if(message==0x84){handled=true;return -1;}if(message==0x21){handled=true;return 3;}return 0; });
            }
            var point=new Native.Point {X=bounds.Left,Y=bounds.Top};ScreenToClient(owner,ref point);
            Native.SetWindowPos(source.Handle,1,point.X,point.Y,bounds.Width,bounds.Height,0x0050);
            var transform=source.CompositionTarget.TransformFromDevice;
            var size=transform.Transform(new Point(bounds.Width,bounds.Height));
            root.Width=size.X;root.Height=size.Y;
            image.ImageSource=wallpaper.Image;
            image.Viewbox=new Rect((bounds.Left-wallpaper.Bounds.Left)/wallpaper.Bounds.Width,(bounds.Top-wallpaper.Bounds.Top)/wallpaper.Bounds.Height,
                bounds.Width/wallpaper.Bounds.Width,bounds.Height/wallpaper.Bounds.Height);
            root.Measure(new Size(size.X,size.Y));root.Arrange(new Rect(0,0,size.X,size.Y));
            UpdateClip();
        }
        finally{if(previous!=0)SetThreadDpiAwarenessContext(previous);}
    }
    public void Dispose(){if(source is not null){if(!source.IsDisposed){source.RootVisual=null;source.Dispose();}source=null;}image.ImageSource=null;}
    [DllImport("user32.dll")] private static extern nint GetWindowDpiAwarenessContext(nint window);
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(nint window,ref Native.Point point);
}
