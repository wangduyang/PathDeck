using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace TaskGlass;

internal static class GlassMenus
{
    private static bool installed;
    internal static void Install(Application app)
    {
        if(app.TryFindResource("PopupSurface") is null)
            app.Resources.MergedDictionaries.Add(new ResourceDictionary {Source=new Uri("/PathDeck.Desktop;component/MenuStyles.xaml",UriKind.Relative)});
        if(installed)return;installed=true;
        EventManager.RegisterClassHandler(typeof(ContextMenu),ContextMenu.OpenedEvent,new RoutedEventHandler((sender,_)=>Apply((ContextMenu)sender)));
    }
    private static void Apply(ContextMenu menu)
    {
        var p=Preferences.Load();var material=GlassTheme.Material(p.TaskbarColor,p.Opacity);
        bool dark=material.Foreground==Colors.White;
        menu.Resources["MenuText"]=new SolidColorBrush(material.Foreground);
        menu.Resources["MenuMuted"]=new SolidColorBrush(material.Foreground){Opacity=.7};
        menu.Resources["MenuAccent"]=new SolidColorBrush(dark?Color.FromRgb(136,226,196):Color.FromRgb(29,88,66));
        menu.Resources["MenuHover"]=new SolidColorBrush(dark?Color.FromArgb(28,255,255,255):Color.FromArgb(20,0,0,0));
        menu.Resources["MenuSelected"]=new SolidColorBrush(dark?Color.FromArgb(50,136,226,196):Color.FromArgb(28,29,88,66));
        menu.Resources["MenuEdge"]=GlassTheme.Rim();
        menu.Foreground=(Brush)menu.Resources["MenuText"];
    }
}

public sealed class GlassMenuBackground : Border
{
    private readonly Image pixels=new(){Stretch=Stretch.Fill,IsHitTestVisible=false};
    private readonly BlurEffect blur=new(){RenderingBias=RenderingBias.Performance};
    private readonly GlassTintEffect tint=new();
    private readonly Border material;
    private int generation;
    public GlassMenuBackground()
    {
        IsHitTestVisible=false;pixels.Effect=blur;material=new Border{Child=pixels,Effect=tint};Child=material;
        Loaded+=(_,_)=>Refresh();
        Unloaded+=(_,_)=>{generation++;pixels.Source=null;};
        SizeChanged+=(_,_)=>Clip=new RectangleGeometry(new Rect(RenderSize),CornerRadius.TopLeft,CornerRadius.TopLeft);
    }
    protected override Size MeasureOverride(Size constraint)=>new();
    private async void Refresh()
    {
        int request=++generation;
        var p=Preferences.Load();var palette=GlassTheme.Material(p.TaskbarColor,p.Opacity);
        tint.Apply(palette);blur.Radius=p.Blur*.6;Background=palette.Fallback;
        if(PresentationSource.FromVisual(this) is not HwndSource source||ActualWidth<=0||ActualHeight<=0)return;
        var start=PointToScreen(new Point());var end=PointToScreen(new Point(ActualWidth,ActualHeight));
        var bitmap=await GlassSnapshot.CaptureAreaAsync(source.Handle,new Rect(start,end));
        if(request!=generation||!IsLoaded)return;
        pixels.Source=bitmap;if(bitmap is not null)Background=Brushes.Transparent;
    }
}
