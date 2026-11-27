using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;

namespace TaskGlass;

// Read the wallpaper itself, never a screen containing desktop icons or application windows.
internal static class DesktopWallpaper
{
    [ComImport, Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDesktopWallpaper
    {
        void SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitor, [MarshalAs(UnmanagedType.LPWStr)] string file);
        void GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitor, [MarshalAs(UnmanagedType.LPWStr)] out string file);
        void GetMonitorDevicePathAt(uint index, [MarshalAs(UnmanagedType.LPWStr)] out string monitor);
        void GetMonitorDevicePathCount(out uint count);
        void GetMonitorRECT([MarshalAs(UnmanagedType.LPWStr)] string monitor, out Native.Rect bounds);
        void SetBackgroundColor(uint color);
        void GetBackgroundColor(out uint color);
        void SetPosition(int position);
        void GetPosition(out int position);
    }

    internal static BackdropFrame Read()
    {
        var wallpaper = (IDesktopWallpaper)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("C2CF3110-460E-4FC1-B9D0-8A1C0C9CC4BD"))!)!;
        try
        {
            var bounds = Forms.SystemInformation.VirtualScreen;
            // Covers must match the untouched wallpaper pixel-for-pixel when hiding icon rows.
            using var bitmap = new Bitmap(Math.Max(1,bounds.Width),Math.Max(1,bounds.Height),System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using(var drawing=Graphics.FromImage(bitmap))
            {
                wallpaper.GetBackgroundColor(out uint background);
                drawing.Clear(System.Drawing.Color.FromArgb((int)(background&255),(int)((background>>8)&255),(int)((background>>16)&255)));
                drawing.Transform=new System.Drawing.Drawing2D.Matrix(1,0,0,1,-bounds.Left,-bounds.Top);
                drawing.InterpolationMode=InterpolationMode.HighQualityBilinear;
                wallpaper.GetPosition(out int position);wallpaper.GetMonitorDevicePathCount(out uint count);
                for(uint i=0;i<count;i++)
                {
                    wallpaper.GetMonitorDevicePathAt(i,out string monitor);wallpaper.GetMonitorRECT(monitor,out var monitorBounds);
                    wallpaper.GetWallpaper(monitor,out string file);
                    if(string.IsNullOrEmpty(file))continue;
                    using var image=System.Drawing.Image.FromFile(file);
                    var target=position==5?new System.Windows.Rect(bounds.Left,bounds.Top,bounds.Width,bounds.Height):
                        new System.Windows.Rect(monitorBounds.Left,monitorBounds.Top,monitorBounds.Width,monitorBounds.Height);
                    var saved=drawing.Save();drawing.SetClip(new Rectangle(monitorBounds.Left,monitorBounds.Top,monitorBounds.Width,monitorBounds.Height));
                    if(position==1)
                    {
                        using var tile=new TextureBrush(image,WrapMode.Tile);tile.TranslateTransform(bounds.Left,bounds.Top);
                        drawing.FillRectangle(tile,monitorBounds.Left,monitorBounds.Top,monitorBounds.Width,monitorBounds.Height);
                    }
                    else
                    {
                        var rect=Placement(new System.Windows.Size(image.Width,image.Height),target,position);
                        drawing.DrawImage(image,new RectangleF((float)rect.X,(float)rect.Y,(float)rect.Width,(float)rect.Height));
                    }
                    drawing.Restore(saved);
                }
            }
            var bytes=new byte[bitmap.Width*bitmap.Height*4];
            var locked=bitmap.LockBits(new Rectangle(0,0,bitmap.Width,bitmap.Height),ImageLockMode.ReadOnly,System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try{for(int y=0;y<bitmap.Height;y++)Marshal.Copy(locked.Scan0+y*locked.Stride,bytes,y*bitmap.Width*4,bitmap.Width*4);}
            finally{bitmap.UnlockBits(locked);}
            var source=BitmapSource.Create(bitmap.Width,bitmap.Height,96,96,PixelFormats.Bgra32,null,bytes,bitmap.Width*4);source.Freeze();
            return new BackdropFrame(source,new System.Windows.Rect(bounds.Left,bounds.Top,bounds.Width,bounds.Height));
        }
        finally{Marshal.ReleaseComObject(wallpaper);}
    }

    internal static System.Windows.Rect Placement(System.Windows.Size image,System.Windows.Rect target,int position)
    {
        if(position==2)return target;
        double scale=position==0?1:position==3?Math.Min(target.Width/image.Width,target.Height/image.Height):Math.Max(target.Width/image.Width,target.Height/image.Height);
        double width=image.Width*scale,height=image.Height*scale;
        return new System.Windows.Rect(target.Left+(target.Width-width)/2,target.Top+(target.Height-height)/2,width,height);
    }
}
