using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using QuickFileDesk;

internal static class DragRenderingChecks
{
    [StructLayout(LayoutKind.Sequential)] private struct Message
    { internal nint Window; internal uint Id; internal nuint WParam; internal nint LParam; internal uint Time; internal int X,Y; internal uint Private; }
    [DllImport("user32.dll")] private static extern bool PeekMessage(out Message message,nint window,uint first,uint last,uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")] private static extern nint DispatchMessage(ref Message message);
    [DllImport("user32.dll",EntryPoint="SendMessageW")] private static extern nint SendMessage(nint window,uint message,nint wp,nint lp);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window,nint after,int x,int y,int width,int height,uint flags);
    internal static void Pump(int milliseconds,Action? step=null)
    {
        var clock=Stopwatch.StartNew();
        while(clock.ElapsedMilliseconds<milliseconds)
        {
            step?.Invoke();
            while(PeekMessage(out var message,0,0,0,1)) { TranslateMessage(ref message);DispatchMessage(ref message); }
            Thread.Sleep(1);
        }
    }
    internal static void Run(string[] args)
    {
        using var appearance=new SharedAppearance((QuickFileDesk.App)Application.Current);
        appearance.Apply(new TaskGlass.Preferences(10,24,false,49));
        var window=new Window {Title="PathDeck · 连续移动检查",Width=1260,Height=780,Left=160,Top=120,ShowActivated=false,ShowInTaskbar=false,
            Content=new TextBlock {Text="仅测试窗口：背景绘制",Margin=new Thickness(30)} };
        var glass=new WindowGlass(window,appearance);
        var layer=(Border)glass.Layers.Children[0];
        var surface=(Image)layer.Child;
        Console.WriteLine($"render tier={RenderCapability.Tier>>16}, pixel shader 2={RenderCapability.IsPixelShaderVersionSupported(2,0)}, mode={RenderOptions.ProcessRenderMode}");
        int textures=0;
        var property=DependencyPropertyDescriptor.FromProperty(Image.SourceProperty,typeof(Image));
        EventHandler changed=(_,_)=>textures++;
        property.AddValueChanged(surface,changed);
        try
        {
            window.Show();Pump(500);
            for(int i=0;i<20&&surface.Source is null;i++)Pump(100);
            Pump(200);
            var handle=new WindowInteropHelper(window).Handle;
            var frames=new List<double>();
            var stepTimes=new List<double>();
            var time=Stopwatch.StartNew();long next=0;int viewChanges=0;
            EventHandler frame=(_,_)=>frames.Add(time.Elapsed.TotalMilliseconds);
            CompositionTarget.Rendering+=frame;
            textures=0;
            long bytes=GC.GetTotalAllocatedBytes(true);
            using var process=Process.GetCurrentProcess();var cpu=process.TotalProcessorTime;
            SendMessage(handle,0x0231,0,0);
            try
            {
                Pump(1600,()=>
                {
                    if(time.ElapsedMilliseconds<next)return;
                    next=time.ElapsedMilliseconds+8;
                    var step=Stopwatch.StartNew();
                    var oldView=((TaskGlass.BackdropImage)surface).Viewport;
                    SetWindowPos(handle,0,160+(int)(90*Math.Sin(time.Elapsed.TotalSeconds*5)),120,0,0,0x0015);
                    if(oldView!=((TaskGlass.BackdropImage)surface).Viewport)viewChanges++;
                    stepTimes.Add(step.Elapsed.TotalMilliseconds);
                });
            }
            finally {CompositionTarget.Rendering-=frame;}
            long allocated=GC.GetTotalAllocatedBytes(true)-bytes;
            var intervals=frames.Zip(frames.Skip(1),(one,two)=>two-one).Order().ToArray();
            stepTimes.Sort();
            Console.WriteLine($"drag: new textures={textures}, viewport updates={viewChanges}, allocated={allocated/1048576.0:F2}MiB, CPU={(process.TotalProcessorTime-cpu).TotalMilliseconds:F0}ms, frames={frames.Count}, frame p95={(intervals.Length>0?intervals[(int)(intervals.Length*.95)]:0):F2}ms, move p95={stepTimes[(int)(stepTimes.Count*.95)]:F2}ms");
            int draggingTextures=textures;
            SendMessage(handle,0x0232,0,0);Pump(300);
            if(draggingTextures>1) throw new Exception("Dragging must reuse a background texture instead of continuously uploading screenshots.");
        }
        finally {property.RemoveValueChanged(surface,changed);window.Close();}
    }
}
