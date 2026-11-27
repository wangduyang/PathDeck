using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Windows.Devices.Radios;
using Forms = System.Windows.Forms;

namespace TaskGlass;

internal sealed class ControlCenterWindow : GlassPanel
{
    private readonly Func<Preferences> preferences;
    private readonly TextBlock network = new(), battery = new(), message = new(), audioLabel = new(), brightnessLabel = new();
    private readonly Button wifi = new(), bluetooth = new(), mute = new(), pause = new();
    private readonly Slider volume = new() { Minimum=0,Maximum=100,IsEnabled=false }, brightness = new() { Minimum=0,Maximum=100,IsEnabled=false };
    private readonly DispatcherTimer poll = new() { Interval=TimeSpan.FromSeconds(1) };
    private Radio? wifiRadio, bluetoothRadio;
    private BrightnessState display = new(false,0,null);
    private bool refreshing, brightnessWriting;
    private (BrightnessState Display,double Value,int Revision)? pendingBrightness;
    private readonly Func<bool> paused;
    private int deviceRevision;
    private int pollCount;
    private bool switchingRadio;
    internal ControlCenterWindow(Func<Preferences> preferences, Action togglePause, Func<bool> paused) : base("控制中心",400,720)
    {
        this.preferences=preferences; this.paused=paused;
        AnimationOrigin=new Point(.95,0);
        System.Windows.Automation.AutomationProperties.SetName(volume,"音量");
        System.Windows.Automation.AutomationProperties.SetName(brightness,"屏幕亮度");
        var scroll=new ScrollViewer { VerticalScrollBarVisibility=ScrollBarVisibility.Auto, HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled };
        var body=new StackPanel(); scroll.Content=body; ContentHost.Children.Add(scroll);
        network.FontSize=13; network.Margin=new Thickness(0,4,0,16); body.Children.Add(network);
        var tiles=new UniformGrid { Columns=2,Margin=new Thickness(-4,0,-4,12) }; body.Children.Add(tiles);
        foreach(var button in new[]{wifi,bluetooth}) { button.MinHeight=84;button.Margin=new Thickness(4);tiles.Children.Add(button); }
        wifi.Click+=async(_,_)=>await SwitchRadio(wifiRadio); bluetooth.Click+=async(_,_)=>await SwitchRadio(bluetoothRadio);
        var links=new StackPanel {Orientation=Orientation.Horizontal};body.Children.Add(links);
        void Link(Panel parent,string label,string uri)
        {var button=new Button {Content=label,Margin=new Thickness(0,0,6,8),Padding=new Thickness(10,5,10,5),FontSize=12};button.Click+=(_,_)=>SystemControls.OpenSettings(uri);parent.Children.Add(button);}
        Link(links,"网络设置","ms-settings:network-wifi");Link(links,"蓝牙设置","ms-settings:bluetooth");
        audioLabel.Margin=new Thickness(0,14,0,0);body.Children.Add(audioLabel);body.Children.Add(volume);
        mute.Content="静音";mute.HorizontalAlignment=HorizontalAlignment.Left;mute.Click+=(_,_)=> {var audio=SystemControls.ReadAudio();if(audio.Available&&!SystemControls.SetMute(!audio.Muted))message.Text="音频设备暂不可用。";ReadAudio();};body.Children.Add(mute);
        volume.ValueChanged+=(_,_)=> {if(!refreshing&&!SystemControls.SetVolume(volume.Value))message.Text="音量修改失败，请检查当前播放设备。";};
        brightnessLabel.Margin=new Thickness(0,22,0,0);body.Children.Add(brightnessLabel);body.Children.Add(brightness);
        brightness.ValueChanged+=async(_,_)=> {if(refreshing||!display.Available)return;pendingBrightness=(display,brightness.Value,deviceRevision);await WriteBrightness();};
        var displayLinks=new StackPanel { Orientation=Orientation.Horizontal }; body.Children.Add(displayLinks);
        Link(displayLinks,"显示设置","ms-settings:display");Link(displayLinks,"系统主题","ms-settings:colors");
        pause.Margin=new Thickness(0,14,0,8);pause.Click+=(_,_)=> {togglePause();ReadPause();};body.Children.Add(pause);
        var focusLinks=new StackPanel {Orientation=Orientation.Horizontal};body.Children.Add(focusLinks);Link(focusLinks,"Windows 专注设置","ms-settings:quietmomentshome");
        battery.TextWrapping=TextWrapping.Wrap;battery.Margin=new Thickness(0,16,0,12);body.Children.Add(battery);
        var powerLinks=new StackPanel {Orientation=Orientation.Horizontal};body.Children.Add(powerLinks);Link(powerLinks,"电源设置","ms-settings:powersleep");
        message.TextWrapping=TextWrapping.Wrap;message.FontSize=12;message.Margin=new Thickness(0,12,0,0);body.Children.Add(message);
        poll.Tick+=(_,_)=> {ReadAudio();ReadRadios();ReadPause();network.Text=SystemControls.NetworkDescription();if(++pollCount%5==0)battery.Text=SystemControls.BatteryDescription();};
        IsVisibleChanged+=(_,_)=> {if(IsVisible)poll.Start();else{poll.Stop();deviceRevision++;}};
        Closed+=(_,_)=>poll.Stop();
    }
    internal void ShowAt(Forms.Screen screen)
    {
        ApplyAppearance(preferences());Height=Math.Min(720,screen.WorkingArea.Height/DisplayGeometry.Scale(screen)-16);
        poll.Interval=TimeSpan.FromSeconds(preferences().PerformancePreset=="节能"?3:preferences().PerformancePreset=="完整效果"?.5:1);
        display=new BrightnessState(false,0,null);pendingBrightness=null;brightness.IsEnabled=false;brightnessLabel.Text="正在查询显示器亮度…";
        var hwnd=new WindowInteropHelper(this).EnsureHandle();double scale=DisplayGeometry.Scale(screen);
        Native.SetWindowPos(hwnd,-1,screen.WorkingArea.Right-(int)(Width*scale)-8,screen.WorkingArea.Top+4,(int)(Width*scale),(int)(Height*scale),0x0010);
        Present();
        _=RefreshDevices(++deviceRevision,screen.DeviceName);
    }
    private void ReadPause()=>pause.Content=paused() ? "继续显示 TaskGlass 通知" : "暂缓 TaskGlass 通知";
    private void ReadAudio()
    {
        var state=SystemControls.ReadAudio();refreshing=true;
        volume.IsEnabled=mute.IsEnabled=state.Available;
        if(!volume.IsMouseCaptureWithin)volume.Value=state.Volume;
        audioLabel.Text=state.Available ? $"音量   {state.Volume:0}%" : "未检测到可用播放设备";
        mute.Content=state.Muted?"取消静音":"静音";refreshing=false;
    }
    private void ReadRadios()
    {
        wifi.Content="Wi-Fi\n"+(wifiRadio is null?"未检测到无线设备":wifiRadio.State==RadioState.On?"已开启":"已关闭");
        bluetooth.Content="蓝牙\n"+(bluetoothRadio is null?"未检测到蓝牙设备":bluetoothRadio.State==RadioState.On?"已开启":"已关闭");
        wifi.IsEnabled=wifiRadio is not null&&!switchingRadio;bluetooth.IsEnabled=bluetoothRadio is not null&&!switchingRadio;
    }
    private async Task SwitchRadio(Radio? radio)
    {
        if(radio is null||switchingRadio)return;switchingRadio=true;ReadRadios();
        try{message.Text=await SystemControls.ToggleRadio(radio);network.Text=SystemControls.NetworkDescription();}
        finally{switchingRadio=false;ReadRadios();}
    }
    private async Task RefreshDevices(int revision,string device)
    {
        ReadAudio();ReadPause();network.Text=SystemControls.NetworkDescription();battery.Text=SystemControls.BatteryDescription();
        var nextWifi=await SystemControls.FindRadio(RadioKind.WiFi);var nextBluetooth=await SystemControls.FindRadio(RadioKind.Bluetooth);
        var nextDisplay=await Task.Run(()=>SystemControls.ReadBrightness(device));
        if(revision!=deviceRevision||!IsVisible)return;
        wifiRadio=nextWifi;bluetoothRadio=nextBluetooth;ReadRadios();display=nextDisplay;
        refreshing=true;brightness.IsEnabled=display.Available;brightness.Value=display.Value;
        brightnessLabel.Text=display.Available?"屏幕亮度":"此显示器不支持软件亮度调节";refreshing=false;
    }
    private async Task WriteBrightness()
    {
        if(brightnessWriting)return;brightnessWriting=true;
        try
        {
            while(pendingBrightness is { } request)
            {
                pendingBrightness=null;if(request.Revision!=deviceRevision)continue;
                bool applied=await Task.Run(()=>SystemControls.SetBrightness(request.Display,request.Value));
                if(!applied&&request.Revision==deviceRevision)message.Text="未能设置亮度，请使用显示器按键或系统设置。";
            }
        }
        finally {brightnessWriting=false;}
    }
}
