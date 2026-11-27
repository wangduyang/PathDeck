using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace TaskGlass;

internal sealed class StatusBarController : IDisposable
{
    private readonly Func<Preferences> preferences;
    private readonly Action launcher, settings, reveal;
    private readonly Action<Forms.Screen> controls;
    private readonly Func<int> count;
    private readonly List<TopBar> bars = new();
    private readonly ResourceMonitor monitor = new();
    private readonly DispatcherTimer tick = new();
    private GlassPanel? popup;
    private TextBlock? resourceText;
    private ResourceState latest = new(null,0,0,null);
    private bool disposed;
    internal int Count=>bars.Count;
    internal StatusBarController(Func<Preferences> preferences,Action launcher,Action<Forms.Screen> controls,Action settings,Func<int> count,Action reveal)
    {
        this.preferences=preferences;this.launcher=launcher;this.controls=controls;this.settings=settings;this.count=count;this.reveal=reveal;
        tick.Tick+=(_,_)=>Refresh();Microsoft.Win32.SystemEvents.DisplaySettingsChanged+=DisplayChanged;
    }
    private void DisplayChanged(object? sender,EventArgs e)=>Application.Current.Dispatcher.BeginInvoke(()=> {if(!disposed)Update();});
    internal void Update()
    {
        var p=preferences();
        var screens=Forms.Screen.AllScreens.Where(s=>p.StatusMonitor=="all" || (p.StatusMonitor=="primary" ? s.Primary : s.DeviceName==p.StatusMonitor)).ToArray();
        if(screens.Length==0)screens=new[]{Forms.Screen.PrimaryScreen!};
        var names=p.ShowStatusBar?screens.Select(s=>s.DeviceName).ToArray():Array.Empty<string>();
        foreach(var bar in bars.Where(b=>!names.Contains(b.Screen.DeviceName)).ToArray()){bars.Remove(bar);bar.Close();}
        foreach(var screen in screens)
        {
            if(!p.ShowStatusBar)break;
            var bar=bars.FirstOrDefault(b=>b.Screen.DeviceName==screen.DeviceName);
            if(bar is null){bar=new TopBar(this,screen);bars.Add(bar);bar.Show();}bar.Apply(p);
        }
        popup?.ApplyAppearance(p);tick.Interval=TimeSpan.FromSeconds(p.PerformancePreset=="节能"?5:p.PerformancePreset=="完整效果"?.5:1);
        if(bars.Count>0)tick.Start();else {tick.Stop();popup?.Dismiss();}Refresh();
    }
    private void Refresh()
    {
        if(disposed)return;var p=preferences();
        if((popup?.IsVisible==true && popup.Title=="资源占用") || bars.Any(b=>b.ResourcesVisible))latest=monitor.Read();
        foreach(var bar in bars)bar.Refresh(p,latest,count());
        if(resourceText is not null && popup?.IsVisible==true)resourceText.Text=ResourceText();
    }
    private string ResourceText()=> $"CPU\n{(latest.Cpu is double cpu?$"{cpu:0.0}%":"正在采样…")}\n\n内存\n{latest.Memory}% · 已用 {latest.UsedMemoryGb:0.0} GB\n\n磁盘吞吐\n{(latest.DiskBytes is double disk?$"{disk/1048576:0.00} MB/s":"当前不可用")}";
    private void ShowPopup(string kind,Forms.Screen screen)
    {
        if(popup is not null && popup.Title==kind)
        {
            if(popup.IsVisible && !popup.IsClosing){popup.Dismiss();return;}
            popup.ApplyAppearance(preferences());PlacePopup(screen);popup.Present();return;
        }
        popup?.Close();popup=null;resourceText=null;
        popup=new GlassPanel(kind,kind=="日历"?424:370,kind=="日历"?500:430);
        popup.AnimationOrigin=new Point(.5,0);
        if(kind=="日历")
        {
            popup.ContentHost.Children.Add(new CalendarPanel());
        }
        else {resourceText=new TextBlock {Text=ResourceText(),FontSize=17,LineHeight=27};popup.ContentHost.Children.Add(resourceText);}
        popup.ApplyAppearance(preferences());
        PlacePopup(screen);popup.Present();
    }
    private void PlacePopup(Forms.Screen screen)
    {
        if(popup is null)return;double scale=DisplayGeometry.Scale(screen);popup.Height=Math.Min(popup.Title=="日历"?500:430,screen.WorkingArea.Height/scale-16);
        int width=(int)(popup.Width*scale),left=popup.Title=="日历"?screen.Bounds.Left+(screen.Bounds.Width-width)/2:screen.WorkingArea.Right-width-8;
        Native.SetWindowPos(new WindowInteropHelper(popup).EnsureHandle(),-1,left,screen.WorkingArea.Top+4,width,(int)(popup.Height*scale),0x0010);
    }
    internal static string DateText(DateTime now,bool seconds,bool lunar)
    {
        string date=now.ToString(seconds?"M月d日 ddd  HH:mm:ss":"M月d日 ddd  HH:mm",CultureInfo.GetCultureInfo("zh-CN"));
        if(!lunar)return date;
        try
        {
            var calendar=new ChineseLunisolarCalendar();int year=calendar.GetYear(now),month=calendar.GetMonth(now),leap=calendar.GetLeapMonth(year);
            bool isLeap=leap>0&&month==leap;if(leap>0&&month>=leap)month--;
            return date+$"  ·  农历{(isLeap?"闰":"")}{month}月{calendar.GetDayOfMonth(now)}日";
        }
        catch(ArgumentOutOfRangeException){return date;}
    }
    public void Dispose()
    {disposed=true;tick.Stop();Microsoft.Win32.SystemEvents.DisplaySettingsChanged-=DisplayChanged;foreach(var bar in bars.ToArray())bar.Close();bars.Clear();popup?.Close();monitor.Dispose();}

    private sealed class TopBar : Window
    {
        [StructLayout(LayoutKind.Sequential)] private struct AppBarData { internal uint Size;internal nint Window;internal uint Callback,Edge;internal Native.Rect Rect;internal nint Param; }
        [DllImport("shell32.dll")] private static extern nuint SHAppBarMessage(uint message,ref AppBarData data);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
        [DllImport("user32.dll")] private static extern nint GetKeyboardLayout(uint thread);
        private readonly StatusBarController owner;
        internal Forms.Screen Screen {get;private set;}
        private readonly Grid content=new();
        private readonly StackPanel left=new(){Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center};
        private readonly StackPanel center=new(){Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center};
        private readonly StackPanel right=new(){Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,VerticalAlignment=VerticalAlignment.Center};
        private readonly Image sample=new(){Stretch=Stretch.Fill,IsHitTestVisible=false};
        private readonly BlurEffect blur=new(){RenderingBias=RenderingBias.Performance};
        private readonly GlassTintEffect tintEffect=new();
        private readonly Border backdropLayer;
        private readonly DispatcherTimer sampleTimer=new(){Interval=TimeSpan.FromMilliseconds(200)};
        private readonly Dictionary<string,Button> buttons=new();
        private readonly Border glass;
        private bool registered,positioning;
        private nint handle;
        private uint callback;
        internal bool ResourcesVisible => buttons.ContainsKey("Resources") && Screen.Bounds.Width/DisplayGeometry.Scale(Screen)>=1100 && !Native.CoveredByFullscreen(handle);
        private readonly DispatcherTimer positionTimer=new(){Interval=TimeSpan.FromMilliseconds(100)};
        internal TopBar(StatusBarController owner,Forms.Screen screen)
        {
            this.owner=owner;Screen=screen;Title="PathDeck 顶部状态栏";WindowStyle=WindowStyle.None;ResizeMode=ResizeMode.NoResize;ShowInTaskbar=false;
            AllowsTransparency=true;Background=Brushes.Transparent;Topmost=true;ShowActivated=false;Height=42;
            Resources.MergedDictionaries.Add(new ResourceDictionary {Source=new Uri("/PathDeck.Desktop;component/SettingsStyles.xaml",UriKind.Relative)});
            content.ColumnDefinitions.Add(new ColumnDefinition());content.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});content.ColumnDefinitions.Add(new ColumnDefinition());
            content.Children.Add(left);Grid.SetColumn(center,1);content.Children.Add(center);Grid.SetColumn(right,2);content.Children.Add(right);
            glass=new Border {CornerRadius=new CornerRadius(15),Padding=new Thickness(12,0,12,0),BorderThickness=new Thickness(1),BorderBrush=new SolidColorBrush(Color.FromArgb(80,181,207,199)),Child=content};
            var layers=new Grid {Margin=new Thickness(12,3,12,3)};sample.Effect=blur;backdropLayer=new Border{Child=sample,Effect=tintEffect,IsHitTestVisible=false};layers.Children.Add(backdropLayer);layers.Children.Add(glass);Content=layers;
            layers.SizeChanged+=(_,_)=>layers.Clip=new RectangleGeometry(new Rect(0,0,layers.ActualWidth,layers.ActualHeight),15,15);
            sampleTimer.Tick+=(_,_)=>{sampleTimer.Stop();if(IsVisible){sample.Source=GlassSnapshot.Capture(this);ApplyMaterial(owner.preferences());}};
            Loaded+=(_,_)=>sampleTimer.Start();
            SourceInitialized+=(_,_)=>
            {
                handle=new WindowInteropHelper(this).Handle;callback=RegisterWindowMessage("TaskGlass.StatusBar.Position");
                long style=Native.GetWindowLong(handle,-20).ToInt64();Native.SetWindowLong(handle,-20,(nint)(style|0x80));
                HwndSource.FromHwnd(handle)!.AddHook(Hook);var data=Data();registered=SHAppBarMessage(0,ref data)!=0;Position();
            };
            positionTimer.Tick+=(_,_)=>{positionTimer.Stop();Position();};
            Closed+=(_,_)=>{positionTimer.Stop();sampleTimer.Stop();sample.Source=null;if(registered){var data=Data();SHAppBarMessage(1,ref data);registered=false;}};
        }
        private AppBarData Data()=>new(){Size=(uint)Marshal.SizeOf<AppBarData>(),Window=handle,Callback=callback,Edge=1};
        private nint Hook(nint hwnd,int message,nint wp,nint lp,ref bool handled)
        {
            if(message==0x21){handled=true;return 3;}
            if((uint)message==callback && wp==1 && !positioning){positionTimer.Stop();positionTimer.Start();}
            if(message==0x2E0 || message==0x7E){positionTimer.Stop();positionTimer.Start();}
            return 0;
        }
        private void Position()
        {
            if(handle==0||positioning)return;positioning=true;
            try
            {
                Screen=Forms.Screen.AllScreens.FirstOrDefault(s=>s.DeviceName==Screen.DeviceName)??Forms.Screen.PrimaryScreen!;
                int height=(int)Math.Round(42*DisplayGeometry.Scale(Screen));var bounds=Screen.Bounds;
                var data=Data();data.Rect=new Native.Rect{Left=bounds.Left,Top=bounds.Top,Right=bounds.Right,Bottom=bounds.Top+height};
                if(registered)
                {
                    SHAppBarMessage(2,ref data);data.Rect.Bottom=data.Rect.Top+height;
                    if(Native.GetWindowRect(handle,out var current)&&current.Equals(data.Rect))return;
                    SHAppBarMessage(3,ref data);
                }
                Native.SetWindowPos(handle,-1,data.Rect.Left,data.Rect.Top,data.Rect.Width,data.Rect.Height,0x0010);
                sampleTimer.Stop();sampleTimer.Start();
            }
            finally{positioning=false;}
        }
        internal void Apply(Preferences p)
        {
            ApplyMaterial(p);sampleTimer.Stop();sampleTimer.Start();
            left.Children.Clear();center.Children.Clear();right.Children.Clear();buttons.Clear();
            var home=new Button{Content="P",ToolTip="PathDeck 设置",Padding=new Thickness(10,0,10,0),MinHeight=30,FontWeight=FontWeights.Bold};
            home.Click+=(_,_)=>owner.settings();left.Children.Add(home);
            foreach(string key in p.StatusComponents)
            {
                var button=new Button{Tag=key,MinHeight=30,Padding=new Thickness(12,0,12,0),Margin=new Thickness(3,0,0,0),FontSize=12};
                button.Click+=(_,_)=> {if(key=="Launcher")owner.launcher();else if(key=="Controls")owner.controls(Screen);else if(key=="Clock")owner.ShowPopup("日历",Screen);else if(key=="Resources")owner.ShowPopup("资源占用",Screen);else if(key=="Notifications")owner.reveal();else if(key=="Input")SystemControls.SwitchInputLanguage();else if(key=="Tray")SystemControls.OpenTray();};
                if(key=="Launcher"&&!p.LauncherEnabled||key=="Controls"&&!p.ControlCenterEnabled)continue;
                buttons[key]=button;(key=="Launcher"?left:key=="Clock"?center:right).Children.Add(button);
            }
            Refresh(p,owner.latest,owner.count());Position();
        }
        private void ApplyMaterial(Preferences p)
        {
            var material=GlassTheme.Material(p.TaskbarColor,p.Opacity);tintEffect.Apply(material);
            glass.Background=sample.Source is null?material.Fallback:Brushes.Transparent;Foreground=new SolidColorBrush(material.Foreground);
            blur.Radius=p.Blur*.6;sample.Visibility=sample.Source is null||Native.CoveredByFullscreen(handle)?Visibility.Hidden:Visibility.Visible;
            Resources["SettingsTextBrush"]=Foreground;Resources["SettingsMutedBrush"]=Foreground;
            Resources["SettingsSurfaceBrush"]=GlassTheme.Surface(material);
        }
        internal void Refresh(Preferences p,ResourceState state,int notifications)
        {
            bool fullscreen=Native.CoveredByFullscreen(handle);glass.Visibility=fullscreen?Visibility.Hidden:Visibility.Visible;
            sample.Visibility=!fullscreen&&sample.Source is not null?Visibility.Visible:Visibility.Hidden;
            IsHitTestVisible=!fullscreen;
            long style=Native.GetWindowLong(handle,-20).ToInt64();Native.SetWindowLong(handle,-20,(nint)(fullscreen?style|0x20:style&~0x20));
            foreach(var (key,button) in buttons)
                button.Content=key switch
                {
                    "Launcher"=>"▦  启动台", "Clock"=>DateText(DateTime.Now,p.ClockSeconds&&p.PerformancePreset!="节能",p.LunarDate),
                    "Resources"=>$"CPU {(state.Cpu is double cpu?$"{cpu:0}%":"…")}  ·  内存 {state.Memory}%  ·  磁盘 {(state.DiskBytes is double disk?$"{disk/1048576:0.0}M/s":"…")}",
                    "Input"=>InputName(),"Notifications"=>$"通知 {notifications}","Controls"=>"☷  控制中心","Tray"=>"⌃  托盘",_=>key
                };
            double screenWidth=Screen.Bounds.Width/DisplayGeometry.Scale(Screen);
            foreach(string key in new[]{"Resources","Input"})if(buttons.TryGetValue(key,out var button))button.Visibility=screenWidth<1100?Visibility.Collapsed:Visibility.Visible;
            if(screenWidth<850&&buttons.TryGetValue("Clock",out var clock))clock.Content=DateTime.Now.ToString(p.ClockSeconds?"HH:mm:ss":"HH:mm");
        }
        private static string InputName()
        {uint thread=Native.GetWindowThreadProcessId(Native.GetForegroundWindow(),out _);long language=GetKeyboardLayout(thread).ToInt64()&0xFFFF;return language is 0x0804 or 0x0404 or 0x0C04?"中 / EN":"EN / 中";}
    }
}
