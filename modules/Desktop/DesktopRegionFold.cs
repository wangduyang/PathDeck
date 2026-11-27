using System;
using System.Linq;

namespace TaskGlass;

internal sealed class DesktopRegionFold
{
    internal Native.Rect Bounds { get; }
    internal Native.Rect[] Icons { get; }
    internal int Rows { get; }
    internal int CollapsedHeight { get; }
    internal bool CanFold => Rows > 2 && CollapsedHeight < Bounds.Height;
    internal bool Expanded { get; private set; }
    internal double VisibleHeight { get; private set; }
    internal bool Animating => Math.Abs(VisibleHeight-target) > .001;
    private double from,target;
    private long started;
    private long? outsideSince;
    private bool interacting;

    internal DesktopRegionFold(Native.Rect bounds,Native.Rect[] icons)
    {
        Bounds=bounds;
        Icons=icons.Where(icon=>Contains(bounds,(icon.Left+icon.Right)/2,(icon.Top+icon.Bottom)/2)).OrderBy(icon=>icon.Top).ToArray();
        CollapsedHeight=bounds.Height;
        if(Icons.Length>0)
        {
            int tolerance=Math.Max(4,(int)(Icons.Select(icon=>icon.Height).OrderBy(height=>height).ElementAt(Icons.Length/2)*.2));
            int rowTop=Icons[0].Top,secondTop=bounds.Bottom;
            Rows=1;
            foreach(var icon in Icons)
                if(icon.Top-rowTop>tolerance){Rows++;rowTop=icon.Top;if(Rows==2)secondTop=rowTop;}
            int firstBottom=Icons.Where(icon=>icon.Top-Icons[0].Top<=tolerance).Max(icon=>icon.Bottom);
            int padding=Math.Max(6,Icons[0].Top-bounds.Top);
            if(Rows>2)CollapsedHeight=Math.Clamp(Math.Min(firstBottom+padding,secondTop)-bounds.Top,1,bounds.Height);
        }
        Expanded=!CanFold;VisibleHeight=target=from=Expanded?bounds.Height:CollapsedHeight;
    }

    internal bool Expand(long now,bool animate)
    {
        if(!CanFold||Expanded)return false;
        outsideSince=null;SetTarget(true,now,animate);return true;
    }
    internal bool Pointer(bool inside,bool busy,long now,bool animate)
    {
        if(!CanFold)return false;
        // Global mouse/menu state also includes clicks in other windows. Only retain an
        // interaction that began in this region, including a drag that has left its bounds.
        interacting=busy&&(interacting||inside);
        if(inside||interacting)
        {
            outsideSince=null;
            return !Expanded&&Animating&&Expand(now,animate);
        }
        if(!Expanded)return false;
        outsideSince??=now;
        if(now-outsideSince.Value<3000)return false;
        SetTarget(false,now,animate);outsideSince=null;return true;
    }
    private void SetTarget(bool expanded,long now,bool animate)
    {
        Advance(now);Expanded=expanded;from=VisibleHeight;target=expanded?Bounds.Height:CollapsedHeight;started=now;
        if(!animate)VisibleHeight=target;
    }
    internal bool Advance(long now)
    {
        if(!Animating)return false;
        double progress=Math.Clamp((now-started)/(Expanded?260.0:220.0),0,1);
        double value=progress>=1?target:from+(target-from)*(1-Math.Pow(1-progress,3));
        if(value==VisibleHeight)return false;VisibleHeight=value;return true;
    }
    internal void FinishAnimation()=>VisibleHeight=target;
    internal static bool Contains(Native.Rect bounds,int x,int y)=>x>=bounds.Left&&x<bounds.Right&&y>=bounds.Top&&y<bounds.Bottom;
}
