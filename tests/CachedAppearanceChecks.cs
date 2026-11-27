using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TaskGlass;

internal static class CachedAppearanceChecks
{
    private const BindingFlags All=BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public;
    private static void Check(bool value,string label){if(!value)throw new Exception(label);}
    internal static void Run()
    {
        var placement=BackdropImage.TexturePlacement(new Rect(-1920,0,1920,1080),new Rect(-900,120,800,600),new Size(400,300));
        Check(placement==new Rect(-510,-60,960,540),"Viewport must preserve screen coordinates on negative-position monitors and mixed DPI.");
        var moved=BackdropImage.TexturePlacement(new Rect(-1920,0,1920,1080),new Rect(-880,120,800,600),new Size(400,300));
        Check(moved.Left-placement.Left==-10,"Moving a window must move the sampled scene in the opposite direction at the correct DPI scale.");
        var bitmap=BitmapSource.Create(4,1,96,96,PixelFormats.Bgra32,null,new byte[]{255,0,0,255,0,255,0,255,0,0,255,255,255,255,255,255},16);bitmap.Freeze();
        var image=new BackdropImage {Source=bitmap};
        typeof(BackdropImage).GetField("frame",All)!.SetValue(image,new BackdropFrame(bitmap,new Rect(0,0,4,1)));
        typeof(BackdropImage).GetField("<Viewport>k__BackingField",All)!.SetValue(image,new Rect(1,0,2,1));
        image.Measure(new Size(80,40));image.Arrange(new Rect(0,0,80,40));
        var rendered=new RenderTargetBitmap(80,40,96,96,PixelFormats.Pbgra32);rendered.Render(image);
        var pixels=new byte[80*40*4];rendered.CopyPixels(pixels,80*4,0);
        Check(pixels[(20*80+20)*4+1]>240&&pixels[(20*80+60)*4+2]>240,"Cached viewport must display the actual matching image pixels, not stretch the entire monitor into the window.");

        var preferences=Preferences.Normalize(new Preferences(33,18,false,11){TaskbarColor="#315E82",NotificationColor="#FFFFFF",DesktopColor="#FFFFFF"});
        Check(preferences.NotificationColor==preferences.TaskbarColor&&preferences.DesktopColor==preferences.TaskbarColor,"Notifications and desktop regions must follow the unified taskbar color, including old imported preferences.");
        var region=new DesktopRegionWindow(0);
        var notice=new NoticeWindow();
        try
        {
            region.SetAppearance(preferences);
            var fill=(SolidColorBrush)region.Content.GetType().GetField("fill",All)!.GetValue(region.Content)!;
            Check(fill.Color==Color.FromArgb(84,49,94,130),"Desktop region background must use the taskbar tint and opacity rather than the old fixed alpha.");
            Check((double)region.Content.GetType().GetField("radius",All)!.GetValue(region.Content)! == 18,"Desktop regions must follow the taskbar corner radius.");
            notice.SetColor(preferences.TaskbarColor);notice.SetMaterialOpacity(preferences.Opacity);notice.SetBlur(preferences.Blur);
            var material=GlassTheme.Material(preferences.TaskbarColor,preferences.Opacity);
            var tint=(GlassTintEffect)typeof(NoticeWindow).GetField("tintEffect",All)!.GetValue(notice)!;
            Check(tint.TintBO==new Point(material.Tint.B/255.0,material.TintOpacity)&&tint.Tone.X==material.LuminosityOpacity,"Notification tint and luminosity must match the taskbar material calculation.");
            Check(notice.Blur==preferences.Blur&&((SolidColorBrush)notice.Foreground).Color==material.Foreground,"Notifications must keep the shared blur strength and readable foreground.");
        }
        finally{region.Close();notice.Close();}
        Console.WriteLine("PASS: cached screen-to-window pixel mapping, negative coordinates/DPI, shared notification material and desktop tint/opacity/radius. Synthetic data only.");
    }
}
