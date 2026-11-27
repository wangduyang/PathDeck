using TaskGlass;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class DesktopFoldingChecks
{
    [DllImport("user32.dll")]private static extern nint GetParent(nint window);
    private static void Check(bool value,string message){if(!value)throw new Exception(message);}
    private static Native.Rect Rect(int x,int y,int width,int height)=>new(){Left=x,Top=y,Right=x+width,Bottom=y+height};
    private static DesktopRegionFold Region(int rows,int left=-500,int top=70)
    {
        var icons=Enumerable.Range(0,rows).SelectMany(row=>Enumerable.Range(0,3).Select(column=>Rect(left+12+column*90,top+12+row*100,76,80))).ToArray();
        return new DesktopRegionFold(Rect(left,top,280,rows*100+4),icons);
    }
    internal static void Run()
    {
        var two=Region(2);Check(two.Rows==2&&!two.CanFold&&two.Expanded&&two.VisibleHeight==two.Bounds.Height,"Two-row regions must remain fully expanded.");
        var empty=new DesktopRegionFold(Rect(0,0,300,300),Array.Empty<Native.Rect>());Check(!empty.CanFold&&empty.Expanded,"An empty or unavailable layout must remain open.");
        var jitter=new DesktopRegionFold(Rect(0,0,300,210),new[]{Rect(12,12,76,80),Rect(102,17,76,70),Rect(12,112,76,80),Rect(102,116,76,76)});
        Check(jitter.Rows==2&&!jitter.CanFold,"Small alignment differences and multiline labels must not create extra rows.");
        var scaled=new DesktopRegionFold(Rect(-800,-600,560,608),Enumerable.Range(0,3).Select(row=>Rect(-776,-576+row*200,152,160)).ToArray());
        Check(scaled.Rows==3&&scaled.CollapsedHeight==208,"Row detection must scale with icon geometry across negative monitor coordinates.");
        var fold=Region(3);Check(fold.Rows==3&&fold.CanFold&&!fold.Expanded,"Three-row regions must start collapsed.");
        Check(fold.CollapsedHeight>=92&&fold.CollapsedHeight<=112&&fold.VisibleHeight==fold.CollapsedHeight,"Only the first complete row may remain visible, including negative screen coordinates.");
        fold.Expand(1000,true);fold.Advance(1130);double middle=fold.VisibleHeight;
        Check(middle>fold.CollapsedHeight&&middle<fold.Bounds.Height,"Expansion must reveal intermediate heights without scaling icons.");
        fold.Advance(1260);Check(fold.VisibleHeight==fold.Bounds.Height&&!fold.Animating,"Expansion must finish after 260 ms.");
        fold.Pointer(false,false,1300,true);fold.Pointer(false,true,2000,true);fold.Pointer(false,true,3000,true);fold.Pointer(false,false,4299,true);
        Check(fold.Expanded,"Leaving for 2999 ms must not close the region.");
        fold.Pointer(false,false,4300,true);Check(!fold.Expanded&&fold.Animating,"Leaving for 3000 ms must start collapse.");
        fold.Advance(4390);double closing=fold.VisibleHeight;Check(closing>fold.CollapsedHeight&&closing<fold.Bounds.Height,"Collapse must show intermediate heights.");
        fold.Pointer(true,false,4390,true);Check(fold.Expanded&&Math.Abs(fold.VisibleHeight-closing)<.001,"Re-entering mid-collapse must reverse smoothly without a height jump.");
        fold.Advance(4650);fold.Pointer(false,false,4700,true);fold.Pointer(true,false,7600,true);fold.Pointer(false,false,7800,true);fold.Pointer(false,false,10799,true);
        Check(fold.Expanded,"Re-entry must cancel the previous outside deadline.");
        fold.Pointer(true,true,10800,true);fold.Pointer(false,true,14800,true);Check(fold.Expanded,"A drag or native context menu started inside the region must postpone auto-collapse after leaving.");
        fold.Pointer(false,false,15000,true);fold.Pointer(false,false,18000,true);fold.Advance(18220);
        Check(!fold.Expanded&&fold.VisibleHeight==fold.CollapsedHeight&&!fold.Animating,"Collapse must finish after 220 ms.");
        fold.Expand(19000,false);Check(fold.VisibleHeight==fold.Bounds.Height&&!fold.Animating,"Disabled animation must expand immediately.");
        fold.Pointer(false,false,19100,false);fold.Pointer(false,false,22100,false);Check(fold.VisibleHeight==fold.CollapsedHeight,"Disabled animation must still honour the three-second delay.");
        Console.WriteLine("PASS: desktop rows, first-row-only collapse, 3-second delay, pointer cancellation, interaction pause and reversible easing.");
        NativeCover();
    }
    private static void NativeCover()
    {
        var owner=new Window{Left=-30000,Top=-30000,Width=500,Height=450,ShowActivated=false,ShowInTaskbar=false};
        DesktopRegionWindow? window=null;HwndSource? icon=null;
        nint original=0;
        try
        {
            owner.Show();nint parent=new WindowInteropHelper(owner).Handle;
            icon=new HwndSource(new HwndSourceParameters("Synthetic icon view"){ParentWindow=parent,WindowStyle=0x40000000|0x10000000,Width=400,Height=400});
            icon.RootVisual=new Border{Background=Brushes.Red};
            original=Native.CreateRoundRectRgn(0,0,400,400,12,12);nint clone=Native.CreateRectRgn(0,0,0,0);Native.CombineRgn(clone,original,0,5);Native.SetWindowRgn(icon.Handle,clone,true);
            var bounds=Rect(-29960,-29940,280,304);
            var icons=Enumerable.Range(0,3).SelectMany(row=>Enumerable.Range(0,3).Select(column=>Rect(bounds.Left+12+column*90,bounds.Top+12+row*100,76,80))).ToArray();
            window=new DesktopRegionWindow(parent);window.SetAppearance(new Preferences(10,24,false,49));window.SetRegion(bounds,icons);window.Place();
            Check(!window.CanFold&&window.VisibleHeight==bounds.Height,"Wallpaper failure must leave native icons fully available.");
            var pixels=Enumerable.Repeat(new byte[]{140,85,30,255},400*400).SelectMany(value=>value).ToArray();
            var image=BitmapSource.Create(400,400,96,96,PixelFormats.Bgra32,null,pixels,400*4);image.Freeze();
            window.SetWallpaper(new BackdropFrame(image,new System.Windows.Rect(bounds.Left,bounds.Top,400,400)));window.Place();window.UpdateLayout();
            Check(window.Collapsed&&window.CanFold,"A ready wallpaper enables the collapsed presentation.");
            nint handle=new WindowInteropHelper(window).Handle,region=Native.CreateRectRgn(0,0,0,0);
            try
            {
                Check(Native.GetWindowRgn(handle,region)!=0,"Collapsed overlay must have a native input region.");
                Check(!Native.PtInRegion(region,30,40),"The native first-row icon must remain outside PathDeck's input region.");
                Check(Native.PtInRegion(region,96,40)&&window.InputAt(new Native.Point{X=bounds.Left+96,Y=bounds.Top+40})==1,"Blank glass must accept expand clicks.");
                Check(Native.PtInRegion(region,30,145)&&window.InputAt(new Native.Point{X=bounds.Left+30,Y=bounds.Top+145})==2,"Covered rows must not activate invisible native icons.");
                Native.GetWindowRgn(icon.Handle,region);Check(Native.EqualRgn(original,region),"Explorer's icon window region must never be changed.");
            }
            finally{Native.DeleteObject(region);}
            var surface=(FrameworkElement)window.Content;var covered=Pixel(surface,.5,.65);
            Check(covered[0]==140&&covered[1]==85&&covered[2]==30&&covered[3]==255,"Hidden rows must show the exact untinted wallpaper, not a stretched glass image.");
            window.Expand(1000,true);window.Advance(1130);Check(window.VisibleHeight>104&&window.VisibleHeight<304,"Native rendering must follow eased intermediate heights.");
            window.Advance(1260);var opened=Pixel(surface,.5,.65);Check(opened[3]==0,"Expanded regions must remove the wallpaper cover over native icons.");
            Check(window.InputAt(new Native.Point{X=bounds.Left+30,Y=bounds.Top+145})==0,"Expanded input must pass through to native icons.");
            long leaving=Environment.TickCount64;
            do
            {
                // Match the controller's global busy signal while clicking outside this region.
                window.Pointer(new Native.Point{X=bounds.Right+20,Y=bounds.Top},false,true);
                window.Advance(Environment.TickCount64);Thread.Sleep(40);
            }while(Environment.TickCount64-leaving<3400);
            Check(window.Collapsed&&!window.Animating&&window.VisibleHeight<bounds.Height,"The native region must finish auto-collapse despite continued mouse activity elsewhere.");
            covered=Pixel(surface,.5,.65);Check(covered[0]==140&&covered[1]==85&&covered[2]==30&&covered[3]==255,"Auto-collapse must restore the actual wallpaper cover over the lower icon rows.");
            window.Close();window=null;
            Check(Native.IsWindow(icon.Handle)&&GetParent(icon.Handle)==parent,"Closing overlays must preserve the native icon view and its parent.");
            region=Native.CreateRectRgn(0,0,0,0);try{Native.GetWindowRgn(icon.Handle,region);Check(Native.EqualRgn(original,region),"Native icon clipping must remain unchanged after closing.");}finally{Native.DeleteObject(region);}
            Console.WriteLine("PASS: offscreen native input holes, exact wallpaper covers, animated reveal, timed auto-collapse during external clicks and unchanged native icon view after closing.");
        }
        finally{window?.Close();icon?.Dispose();owner.Close();if(original!=0)Native.DeleteObject(original);}
    }
    private static byte[] Pixel(FrameworkElement surface,double x,double y)
    {
        surface.UpdateLayout();int width=(int)Math.Ceiling(surface.ActualWidth),height=(int)Math.Ceiling(surface.ActualHeight);
        var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);bitmap.Render(surface);var value=new byte[4];
        bitmap.CopyPixels(new Int32Rect((int)(width*x),(int)(height*y),1,1),value,4,0);return value;
    }
}
