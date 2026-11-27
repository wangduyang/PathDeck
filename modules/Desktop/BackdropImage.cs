using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace TaskGlass;

// Keep the screen texture on the GPU during movement. Only the sampled viewport moves;
// taking a fresh screenshot and uploading a new bitmap for every position change stalls composition.
public sealed class BackdropImage : Image
{
    private const nuint TimerId = 0x5044;
    private Window? window;
    private HwndSource? source;
    private nint handle;
    private BackdropFrame? frame;
    private readonly Image pixels = new() { Stretch = Stretch.Fill };
    private readonly Border material = new() { CacheMode = new BitmapCache { RenderAtScale = .5 } };
    private readonly TranslateTransform offset = new();
    private readonly RectangleGeometry viewportClip = new();
    private bool moving, pending, capturing, refreshAgain, closed;
    private int generation;
    private int failures;
    private bool prepared;
    internal Rect Viewport { get; private set; }
    internal event Action? SnapshotChanged;
    internal Image Pixels => pixels;
    internal Border MaterialLayer => material;

    public BackdropImage()
    {
        Stretch = Stretch.Fill; IsHitTestVisible = false;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.LowQuality);
        material.Child = pixels; material.RenderTransform = offset;
        AddVisualChild(material); AddLogicalChild(material); Clip = viewportClip;
        SizeChanged += (_, _) => UpdateViewport();
    }

    internal void Attach(Window owner)
    {
        window = owner;
        // Cache the complete material before translating it. Moving the cached visual does not
        // invalidate its blur/tint output, unlike changing the input of a window-sized effect.
        pixels.Effect = Effect; SetCurrentValue(EffectProperty, null);
        if (VisualTreeHelper.GetParent(this) is Border parent)
        { material.Effect = parent.Effect; parent.SetCurrentValue(EffectProperty, null); }
        owner.SourceInitialized += (_, _) => Connect();
        if (new WindowInteropHelper(owner).Handle != 0) Connect();
        owner.LocationChanged += (_, _) => PositionChanged();
        owner.SizeChanged += (_, _) => PositionChanged();
        owner.Activated += (_, _) => {if(!prepared)Refresh();};
        owner.Loaded+=(_,_)=>{if(frame is null)Refresh();};
        owner.StateChanged+=(_,_)=>{if(!prepared&&owner.WindowState!=WindowState.Minimized)Refresh();};
        owner.IsVisibleChanged += (_, _) => { if (owner.IsVisible) {if(!prepared)Refresh();} else Clear(); };
        owner.Closed += (_, _) =>
        {
            closed = true; Clear(); source?.RemoveHook(Hook); source = null; handle = 0;
        };
        if (owner.IsVisible) Refresh();
    }

    private void Connect()
    {
        handle = new WindowInteropHelper(window!).Handle;
        source = HwndSource.FromHwnd(handle); source?.AddHook(Hook);
    }

    private void PositionChanged()
    {
        UpdateViewport();
        if(prepared&&CoversVisibleViewport())return;
        if (moving && CoversVisibleViewport()) return;
        // For programmatic moves/resizes, coalesce the sequence and refresh after it settles.
        Schedule(moving ? 16u : 100u, restart: !moving);
    }

    internal void Refresh() {failures=0;Schedule(16, restart: false);}

    internal void Prepare()
    {
        if(closed||window is null||(window.IsVisible&&window.WindowState!=WindowState.Minimized))return;
        bool restoring=window.WindowState==WindowState.Minimized;
        var value=GlassSnapshot.CaptureHiddenScene(window);if(value is null)return;
        generation++;StopTimer();refreshAgain=false;failures=0;prepared=true;
        frame=value;Source=value.Image;UpdateViewport();SnapshotChanged?.Invoke();
        // Keep the prepared scene through initial layout and the DWM show/restore transition.
        _=Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle,new Action(()=>
        {
            prepared=false;
            // The minimizing surface can outlive WPF layout. Sample once more after its animation.
            if(restoring)Schedule(300,restart:true);
        }));
    }

    private bool CoversVisibleViewport()
    {
        if (frame is null) return false;
        var screen = System.Windows.Forms.SystemInformation.VirtualScreen;
        var visible = Rect.Intersect(Viewport, new Rect(screen.Left, screen.Top, screen.Width, screen.Height));
        // Invisible pixels at the edge of the desktop do not require another screen readback.
        return visible.IsEmpty || frame.Bounds.Contains(visible);
    }

    private void Schedule(uint delay, bool restart)
    {
        if (closed || window?.IsVisible != true || window.WindowState == WindowState.Minimized || handle == 0) return;
        if (capturing) { refreshAgain = true; return; }
        if (restart) StopTimer();
        if (!pending) pending = SetTimer(handle, TimerId, delay, 0) != 0;
    }

    private void StopTimer() { if (pending) KillTimer(handle, TimerId); pending = false; }

    private nint Hook(nint hwnd, int message, nint wp, nint lp, ref bool handled)
    {
        if (message == 0x0231)
        {
            moving = true; StopTimer(); UpdateViewport();
            if (frame is null) Refresh();
        }
        else if (message == 0x0232)
        {
            moving = false; UpdateViewport(); Refresh();
        }
        else if (message == 0x0047) UpdateViewport();
        else if (message == 0x0113 && wp == (nint)TimerId)
        {
            bool refresh = pending; StopTimer();
            if (refresh) Capture();
            handled = true;
        }
        return 0;
    }

    private void UpdateViewport()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0 || PresentationSource.FromVisual(this) is null) return;
        var start = PointToScreen(new Point());
        var end = PointToScreen(new Point(ActualWidth, ActualHeight));
        var value = new Rect(start, end);
        if (value == Viewport) return;
        Viewport = value; ArrangeScene(RenderSize);
    }

    private async void Capture()
    {
        if (capturing) { refreshAgain = true; return; }
        if (closed || window?.IsVisible != true || window.WindowState == WindowState.Minimized) return;
        capturing = true; int requestGeneration = generation;bool retry=false;
        try
        {
            var value = await GlassSnapshot.CaptureSceneAsync(window);
            if (closed || requestGeneration != generation || !window.IsVisible || window.WindowState == WindowState.Minimized) return;
            if(value is null){retry=frame is null&&++failures<=2;return;}
            failures=0;frame = value; Source = value.Image;
            UpdateViewport(); SnapshotChanged?.Invoke();
        }
        finally
        {
            capturing = false;
            if(retry&&!closed&&requestGeneration==generation)Schedule(120,restart:false);
            if (refreshAgain && !closed)
            {
                refreshAgain = false;
                if (!moving || !CoversVisibleViewport()) Refresh();
            }
        }
    }

    private void Clear()
    {
        generation++; StopTimer(); refreshAgain = false; moving = false;prepared=false;failures=0;
        frame = null; Source = null; SnapshotChanged?.Invoke();
    }

    protected override Size MeasureOverride(Size constraint) => new();
    protected override Size ArrangeOverride(Size finalSize)
    {
        ArrangeScene(finalSize); return finalSize;
    }

    private void ArrangeScene(Size size)
    {
        viewportClip.Rect = new Rect(size);
        if (frame is null || !ReferenceEquals(Source, frame.Image) || Viewport.Width <= 0 || Viewport.Height <= 0)
        {
            material.Measure(size); material.Arrange(new Rect(size)); offset.X = offset.Y = 0; return;
        }
        var placement = TexturePlacement(frame.Bounds, Viewport, size);
        var sceneSize = placement.Size;
        material.Measure(sceneSize); material.Arrange(new Rect(new Point(), sceneSize));
        offset.X = placement.X; offset.Y = placement.Y;
    }

    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => index == 0 ? material : throw new ArgumentOutOfRangeException(nameof(index));

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SourceProperty && pixels is not null)
        { pixels.Source = Source; InvalidateArrange(); }
    }

    internal static Rect TexturePlacement(Rect scene, Rect viewport, Size size) => new(
        (scene.Left - viewport.Left) * size.Width / viewport.Width,
        (scene.Top - viewport.Top) * size.Height / viewport.Height,
        scene.Width * size.Width / viewport.Width, scene.Height * size.Height / viewport.Height);

    protected override void OnRender(DrawingContext drawing)
    {
        // The child visual holds the cached material; drawing the Image base would paint it twice.
    }

    [DllImport("user32.dll")] private static extern nuint SetTimer(nint hwnd, nuint id, uint milliseconds, nint callback);
    [DllImport("user32.dll")] private static extern bool KillTimer(nint hwnd, nuint id);
}
