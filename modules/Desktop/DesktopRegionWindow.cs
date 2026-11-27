using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace TaskGlass;

internal sealed class DesktopRegionWindow : Window
{
    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPosition
    {
        public nint Window, InsertAfter;
        public int X, Y, Width, Height;
        public uint Flags;
    }
    private readonly RegionSurface surface = new();
    private nint handle;
    private Native.Rect bounds;
    private readonly DesktopBackgroundLayer background;
    private BackdropFrame? wallpaper;
    private Native.Rect[] icons=Array.Empty<Native.Rect>();
    private DesktopRegionFold? fold;
    private int displayedHeight=-1;
    internal event Action? DisplayChanged;
    internal bool BackgroundLive => background.Live;
    internal bool CanFold => fold?.CanFold==true;
    internal bool Collapsed => CanFold&&fold?.Expanded==false;
    internal bool Animating => fold?.Animating==true;
    internal double VisibleHeight => fold?.VisibleHeight??bounds.Height;
    internal bool Owns(nint window)=>window==handle;
    internal DesktopRegionWindow(nint owner)
    {
        background=new DesktopBackgroundLayer(owner);
        Title = "PathDeck 桌面分区"; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true; Background = Brushes.Transparent;
        ShowInTaskbar = false; ShowActivated = false; Focusable = false;
        WindowStartupLocation = WindowStartupLocation.Manual; Left = -30000; Top = -30000; Width = 1; Height = 1;
        Content = surface;
        Closed += (_,_)=>{background.Dispose();surface.SetWallpaper(null,bounds);fold=null;icons=Array.Empty<Native.Rect>();};
        if (owner != 0) new WindowInteropHelper(this).Owner = owner;
        SourceInitialized += (_, _) =>
        {
            handle = new WindowInteropHelper(this).Handle;
            Native.SetWindowLong(handle, -20, (nint)(Native.GetWindowLong(handle, -20).ToInt64() | 0x20 | 0x08000000 | 0x80));
            HwndSource.FromHwnd(handle)?.AddHook((nint hwnd, int message, nint wp, nint lp, ref bool handled) =>
            {
                if (message == 0x0046 && lp != 0)
                {
                    // Owner activation can otherwise promote an owned popup even when its layout has not changed.
                    var position = Marshal.PtrToStructure<WindowPosition>(lp);
                    position.InsertAfter = 1;
                    position.Flags &= ~0x0004u;
                    Marshal.StructureToPtr(position, lp, false);
                }
                if (message == 0x0084)
                {
                    var point=new Native.Point{X=(short)((long)lp&0xFFFF),Y=(short)(((long)lp>>16)&0xFFFF)};
                    handled=true;return InputAt(point)>0?1:-1;
                }
                if (message == 0x0021) { handled = true; return 3; }
                if (message is 0x0201 or 0x0203)
                {
                    var point=new Native.Point{X=bounds.Left+(short)((long)lp&0xFFFF),Y=bounds.Top+(short)(((long)lp>>16)&0xFFFF)};
                    int input=InputAt(point);
                    if(input==1)Expand(Environment.TickCount64,GlassMotion.Active);
                    if(input>0){handled=true;return 0;}
                }
                return 0;
            });
        };
    }
    internal void SetRegion(Native.Rect region,Native.Rect[]? regionIcons=null)
    {
        bool expanded=fold?.Expanded==true;bounds=region;var layout=new DesktopRegionFold(bounds,regionIcons??Array.Empty<Native.Rect>());icons=layout.Icons;
        if(wallpaper is not null){fold=layout;if(expanded)fold.Expand(Environment.TickCount64,false);}
        displayedHeight=-1;surface.SetWallpaper(wallpaper,bounds);
    }
    internal void SetAppearance(Preferences value)
    {
        surface.SetAppearance(value);background.SetAppearance(value);
        if(!GlassMotion.Active&&Animating){fold!.FinishAnimation();UpdateDisplay(true);DisplayChanged?.Invoke();}
    }
    internal void SetWallpaper(BackdropFrame value)
    {
        wallpaper=value;fold??=new DesktopRegionFold(bounds,icons);surface.SetWallpaper(value,bounds);displayedHeight=-1;
    }
    internal void Place()
    {
        if (!IsVisible) Show();
        // Desktop-owned, non-topmost and behind ordinary application windows. No cross-process SetParent.
        Native.SetWindowPos(handle, 1, bounds.Left, bounds.Top, bounds.Width, bounds.Height, 0x0250);
        if(wallpaper is not null){background.Place(bounds,wallpaper);surface.SetBackgroundVisible(background.Live);}
        UpdateDisplay(true);
    }
    internal void Pointer(Native.Point point,bool desktop,bool busy=false)
    {
        var active=bounds;
        if(!Animating)active.Bottom=active.Top+(int)Math.Ceiling(VisibleHeight);
        bool hover=desktop&&DesktopRegionFold.Contains(active,point.X,point.Y);
        surface.Pointer(hover, (double)(point.X - bounds.Left) / Math.Max(1, bounds.Width), (double)(point.Y - bounds.Top) / Math.Max(1, bounds.Height));
        if(fold?.Pointer(hover,busy,Environment.TickCount64,GlassMotion.Active)==true){UpdateDisplay(true);DisplayChanged?.Invoke();}
    }
    internal void Expand(long now,bool animate)
    {if(fold?.Expand(now,animate)==true){UpdateDisplay(true);DisplayChanged?.Invoke();}}
    internal void Advance(long now)
    {if(fold?.Advance(now)==true)UpdateDisplay();}
    // 1 expands blank glass; 2 shields wallpaper-covered rows. Visible icon rectangles pass through.
    internal int InputAt(Native.Point point)
    {
        if(!CanFold||!DesktopRegionFold.Contains(bounds,point.X,point.Y))return 0;
        if(point.Y>=bounds.Top+Math.Ceiling(VisibleHeight))return 2;
        return Collapsed&&!icons.Any(icon=>DesktopRegionFold.Contains(icon,point.X,point.Y))?1:0;
    }
    private void UpdateDisplay(bool force=false)
    {
        if(bounds.Width<=0||bounds.Height<=0)return;
        int height=Math.Clamp((int)Math.Ceiling(VisibleHeight),1,bounds.Height);
        if(!force&&height==displayedHeight)return;displayedHeight=height;
        surface.SetDisplay((double)height/bounds.Height,Collapsed,icons.Length>0?(double)Math.Max(0,bounds.Right-icons.Max(icon=>icon.Right))/bounds.Width:0);
        background.SetVisibleHeight(height);
        if(handle==0)return;
        bool input=CanFold&&(height<bounds.Height||Collapsed);
        long style=Native.GetWindowLong(handle,-20).ToInt64(),next=input?style&~0x20L:style|0x20;
        if(style!=next)Native.SetWindowLong(handle,-20,(nint)next);
        if(!input){Native.SetWindowRgn(handle,0,true);return;}
        nint region=Native.CreateRectRgn(0,0,bounds.Width,bounds.Height);
        try
        {
            foreach(var icon in icons)
            {
                int bottom=Math.Min(icon.Bottom,bounds.Top+height);if(bottom<=icon.Top)continue;
                nint hole=Native.CreateRectRgn(icon.Left-bounds.Left,icon.Top-bounds.Top,icon.Right-bounds.Left,bottom-bounds.Top);
                try{Native.CombineRgn(region,region,hole,4);}finally{Native.DeleteObject(hole);}
            }
            if(Native.SetWindowRgn(handle,region,true)==0)throw new Win32Exception();region=0;
        }
        finally{if(region!=0)Native.DeleteObject(region);}
    }
    private sealed class RegionSurface : FrameworkElement
    {
        private Brush rim = new SolidColorBrush(Color.FromArgb(100, 110, 124, 121));
        private Brush fill = Brushes.Transparent;
        private string currentColor = GlassTheme.TaskbarTint;
        private double opacity = double.NaN, radius = 24;
        private Point light = new(.3, 0);
        private double strength = .18;
        private bool backgroundVisible;
        private ImageBrush? wallpaper;
        private double fraction=1,disclosureGutter;
        private bool disclosure;
        private Brush ink=Brushes.White;
        private static readonly Brush InputFill=new SolidColorBrush(Color.FromArgb(1,0,0,0));
        internal void SetWallpaper(BackdropFrame? frame,Native.Rect bounds)
        {
            wallpaper=frame is null?null:new ImageBrush(frame.Image){Stretch=Stretch.Fill,ViewboxUnits=BrushMappingMode.RelativeToBoundingBox,
                Viewbox=new Rect((bounds.Left-frame.Bounds.Left)/frame.Bounds.Width,(bounds.Top-frame.Bounds.Top)/frame.Bounds.Height,bounds.Width/frame.Bounds.Width,bounds.Height/frame.Bounds.Height)};
            wallpaper?.Freeze();InvalidateVisual();
        }
        internal void SetDisplay(double value,bool canExpand,double gutter)
        {fraction=value;disclosure=canExpand;disclosureGutter=gutter;Cursor=canExpand?System.Windows.Input.Cursors.Hand:null;InvalidateVisual();}
        internal void SetBackgroundVisible(bool value){if(backgroundVisible==value)return;backgroundVisible=value;InvalidateVisual();}
        internal void SetAppearance(Preferences value)
        {
            if (currentColor == value.TaskbarColor && opacity == value.Opacity && radius == value.Radius) return;
            currentColor = value.TaskbarColor; opacity = value.Opacity; radius = value.Radius;
            var color = (Color)ColorConverter.ConvertFromString(currentColor); color.A = (byte)Math.Round(Math.Clamp(opacity, 0, 100) * 255 / 100);
            var brush = new SolidColorBrush(color); brush.Freeze(); fill = brush;
            rim = GlassTheme.TaskbarRim(currentColor);
            ink=new SolidColorBrush(GlassTheme.Material(currentColor,opacity).Foreground);
            InvalidateVisual();
        }
        internal void Pointer(bool hover, double x, double y)
        {
            bool animation = GlassMotion.Active;
            double next = animation && hover ? .38 : .18;
            var point = !animation ? new Point(.3, 0) : hover ? new Point(x, y) : light;
            double movement = Math.Abs(point.X - light.X) + Math.Abs(point.Y - light.Y);
            if (next == strength && (animation ? movement < .006 : movement == 0)) return;
            strength = next; light = point; InvalidateVisual();
        }
        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            if (ActualWidth < 2 || ActualHeight < 2) return;
            double scale = VisualTreeHelper.GetDpi(this).DpiScaleX, pixel = 1 / scale;
            double height=ActualHeight*fraction;
            if(wallpaper is not null&&height<ActualHeight)
            {
                dc.PushClip(new RectangleGeometry(new Rect(0,height,ActualWidth,ActualHeight-height)));
                dc.DrawRectangle(wallpaper,null,new Rect(0,0,ActualWidth,ActualHeight));dc.Pop();
            }
            var rect = new Rect(pixel, pixel, ActualWidth - 2 * pixel, Math.Max(0,height - 2 * pixel));
            double radius = Math.Max(0, Math.Min(this.radius, Math.Min(ActualWidth, height) / 2) - pixel);
            if(disclosure)dc.DrawRoundedRectangle(InputFill,null,rect,radius,radius);
            if(!backgroundVisible)dc.DrawRoundedRectangle(fill, null, rect, radius, radius);
            dc.DrawRoundedRectangle(null, new Pen(rim, pixel), rect, radius, radius);
            var highlight = new RadialGradientBrush(GlassTheme.Highlight, Colors.Transparent)
            { Center = light, GradientOrigin = light, RadiusX = .7, RadiusY = .4 };
            dc.PushOpacity(strength); dc.DrawRoundedRectangle(null, new Pen(highlight, 2 * pixel), rect, radius, radius); dc.Pop();
            if(disclosure)
            {
                double gutter=ActualWidth*disclosureGutter,x=ActualWidth-Math.Max(5,gutter/2),y=height/2,size=Math.Min(4,Math.Max(2,gutter/2-1));
                var pen=new Pen(ink,1.5){StartLineCap=PenLineCap.Round,EndLineCap=PenLineCap.Round};
                dc.DrawLine(pen,new Point(x-size,y-2),new Point(x,y+2));dc.DrawLine(pen,new Point(x,y+2),new Point(x+size,y-2));
            }
        }
    }
}
