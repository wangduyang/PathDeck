using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QuickFileDesk;
using TaskGlass;

internal static class WinEBackdropChecks
{
    private const BindingFlags Fields=BindingFlags.NonPublic|BindingFlags.Instance;
    private static void Check(bool value,string message){if(!value)throw new Exception(message);}
    private static void Pump(int milliseconds)
    {
        var frame=new DispatcherFrame();var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(milliseconds)};
        timer.Tick+=(_,_)=>{timer.Stop();frame.Continue=false;};timer.Start();Dispatcher.PushFrame(frame);
    }
    internal static void Run(App app)
    {
        var preferences=new Preferences(10,24,false,0);preferences.Save();
        using var appearance=new SharedAppearance(app);
        KeepLastBackdrop(appearance);
        var background=new Window{Title="PathDeck · 合成背景检查",Background=Brushes.Lime,WindowStyle=WindowStyle.None,ShowInTaskbar=false,WindowState=WindowState.Maximized,Topmost=true};
        var appearanceField=typeof(App).GetField("appearance",Fields)!;var shortcutField=typeof(App).GetField("shortcut",Fields)!;
        object? oldAppearance=appearanceField.GetValue(app),oldShortcut=shortcutField.GetValue(app);var oldMain=app.MainWindow;
        var open=typeof(App).GetMethod("OpenFromShortcut",Fields)!;
        appearanceField.SetValue(app,appearance);
        // Exercise the actual shortcut entry without installing a hook or sending global Windows keys.
        shortcutField.SetValue(app,RuntimeHelpers.GetUninitializedObject(typeof(WinEShortcut)));
        MainWindow? browser=null;
        try
        {
            background.Show();Pump(180);
            var hidden=new Window{Left=80,Top=80,Width=300,Height=240,ShowActivated=false,ShowInTaskbar=false,Content=new Grid()};
            try
            {
                var prepared=new WindowGlass(hidden,appearance);prepared.Prepare();
                Check(!hidden.IsVisible&&((BackdropImage)((Border)prepared.Layers.Children[0]).Child).Source is BitmapSource,"The glass scene must be ready before the first visible frame.");
            }
            finally{hidden.Close();}
            open.Invoke(app,null);browser=(MainWindow)app.MainWindow;browser.Topmost=true;Pump(350);
            var image=(BackdropImage)((Border)((Grid)browser.Content).Children[0]).Child;
            Check(Samples(image,Colors.Lime),"First shortcut open must sample the synthetic background instead of an opaque fallback or its own window.");
            browser.WindowState=WindowState.Minimized;background.Background=Brushes.Blue;Pump(100);open.Invoke(app,null);Pump(700);
            Check(Samples(image,Colors.Blue),"Restoring through Win+E must refresh the real background.");
            background.Background=Brushes.Red;Pump(100);open.Invoke(app,null);Pump(350);
            Check(Samples(image,Colors.Red),"Invoking Win+E on an already active browser must refresh its background too.");
            Check(browser.Opacity==1&&((FrameworkElement)((Grid)browser.Content).Children[1]).Opacity==1,"Fixing backdrop transparency must preserve opaque foreground controls.");
            Console.WriteLine("PASS: actual shortcut entry cold-open, minimized restore and repeated activation sample the correct synthetic background; foreground stays opaque. No keyboard hooks installed.");
        }
        finally
        {
            browser?.Close();background.Close();shortcutField.SetValue(app,oldShortcut);appearanceField.SetValue(app,oldAppearance);app.MainWindow=oldMain;
        }
    }
    private static void KeepLastBackdrop(SharedAppearance appearance)
    {
        var window=new Window{Left=-30000,Top=-30000,Width=300,Height=240,ShowActivated=false,ShowInTaskbar=false,Content=new Grid()};
        var glass=new WindowGlass(window,appearance);var image=(BackdropImage)((Border)glass.Layers.Children[0]).Child;
        try
        {
            window.Show();Pump(100);
            var pixel=BitmapSource.Create(1,1,96,96,PixelFormats.Bgra32,null,new byte[]{20,90,170,255},4);pixel.Freeze();
            typeof(BackdropImage).GetField("frame",Fields)!.SetValue(image,new BackdropFrame(pixel,new Rect(-32000,-32000,4000,4000)));image.Source=pixel;
            image.Refresh();Pump(180);
            Check(ReferenceEquals(image.Source,pixel),"A transient unavailable capture must retain the last good glass background instead of switching to opaque fallback.");
            window.Hide();Check(image.Source is null,"Hiding must still release the cached scene.");
        }
        finally{window.Close();}
    }
    private static bool Samples(BackdropImage image,Color expected)
    {
        if(typeof(BackdropImage).GetField("frame",Fields)!.GetValue(image) is not BackdropFrame frame)return false;
        var point=image.PointToScreen(new Point(image.ActualWidth*.65,image.ActualHeight*.65));
        int x=(int)((point.X-frame.Bounds.Left)*frame.Image.PixelWidth/frame.Bounds.Width),y=(int)((point.Y-frame.Bounds.Top)*frame.Image.PixelHeight/frame.Bounds.Height);
        if(x<0||y<0||x>=frame.Image.PixelWidth||y>=frame.Image.PixelHeight)return false;
        var pixel=new byte[4];frame.Image.CopyPixels(new Int32Rect(x,y,1,1),pixel,4,0);
        return Math.Abs(pixel[0]-expected.B)<12&&Math.Abs(pixel[1]-expected.G)<12&&Math.Abs(pixel[2]-expected.R)<12;
    }
}
