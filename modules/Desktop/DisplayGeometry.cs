using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Forms = System.Windows.Forms;

namespace TaskGlass;

internal static class DisplayGeometry
{
    [DllImport("user32.dll")] private static extern nint MonitorFromPoint(Native.Point point,uint flags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(nint monitor,int type,out uint x,out uint y);
    internal static double Scale(Forms.Screen screen)
    {
        var point=new Native.Point {X=screen.Bounds.Left+screen.Bounds.Width/2,Y=screen.Bounds.Top+screen.Bounds.Height/2};
        return GetDpiForMonitor(MonitorFromPoint(point,2),0,out uint x,out _)>=0?x/96.0:1;
    }
    internal static Forms.Screen Current()
    {Native.GetCursorPos(out var point);return Forms.Screen.FromPoint(new System.Drawing.Point(point.X,point.Y));}
    internal static void Center(Window window,Forms.Screen screen)
    {
        double scale=Scale(screen);window.Width=Math.Min(window.Width,screen.WorkingArea.Width/scale-24);window.Height=Math.Min(window.Height,screen.WorkingArea.Height/scale-24);
        Native.SetWindowPos(new WindowInteropHelper(window).EnsureHandle(),-1,screen.WorkingArea.Left+(screen.WorkingArea.Width-(int)(window.Width*scale))/2,
            screen.WorkingArea.Top+(screen.WorkingArea.Height-(int)(window.Height*scale))/2,(int)(window.Width*scale),(int)(window.Height*scale),0x0010);
    }
}
