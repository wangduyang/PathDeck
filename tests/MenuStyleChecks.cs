using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using TaskGlass;

internal static class MenuStyleChecks
{
    internal static void Run()
    {
        var menu=new ContextMenu();menu.Items.Add(new MenuItem {Header="设置",InputGestureText="Ctrl+,"});
        var submenu=new MenuItem {Header="打开方式"};submenu.Items.Add(new MenuItem{Header="外部应用"});menu.Items.Add(submenu);
        menu.ApplyTemplate();menu.Measure(new Size(340,900));menu.Arrange(new Rect(0,0,340,menu.DesiredSize.Height));menu.UpdateLayout();
        if(!ContainsGlass(menu))throw new Exception("Context menu must use the shared glass background, not a classic opaque menu.");
        if(menu.Items.Count!=2||submenu.Items.Count!=1)throw new Exception("Menu styling must preserve commands and submenus.");
        var host=new Window {Width=100,Height=100,Left=-10000,Top=-10000,ShowInTaskbar=false};
        try
        {
            host.Show();host.Activate();Pump(100);menu.PlacementTarget=host;menu.IsOpen=true;Pump(300);
            if(!menu.IsOpen)throw new Exception("Opening a file context menu must not steal foreground and immediately dismiss itself.");
            menu.IsOpen=false;Pump(50);
            menu.PlacementTarget=null;menu.IsOpen=true;menu.Focus();Pump(300);
            if(!menu.IsOpen)throw new Exception("The unanchored tray menu must remain open for selecting an action.");
        }
        finally {menu.IsOpen=false;host.Close();}
        Console.WriteLine("PASS: shared glass menu retains commands, shortcuts and submenus; file and tray popups remain open until dismissed.");
    }
    private static void Pump(int milliseconds)
    {
        var frame=new DispatcherFrame();var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(milliseconds)};
        timer.Tick+=(_,_)=>{timer.Stop();frame.Continue=false;};timer.Start();Dispatcher.PushFrame(frame);
    }
    private static bool ContainsGlass(DependencyObject item)
    {
        if(item is GlassMenuBackground)return true;
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(item);i++)if(ContainsGlass(VisualTreeHelper.GetChild(item,i)))return true;
        return false;
    }
}
