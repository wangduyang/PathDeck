using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TaskGlass;

internal static class ClockProgressChecks
{
    private const BindingFlags All=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    private static void Check(bool value,string message){if(!value)throw new Exception(message);}
    internal static void Run(string? previewPath=null)
    {
        Preferences Parse(string text)=>Preferences.Normalize(JsonSerializer.Deserialize<Preferences>(text)!);
        var preferences=Parse("{\"WorkStart\":\"09:00\",\"WorkEnd\":\"18:30\"}");
        using var json=JsonDocument.Parse(JsonSerializer.Serialize(preferences));
        Check(json.RootElement.TryGetProperty("WorkStart",out var start)&&start.GetString()=="09:00"&&json.RootElement.GetProperty("WorkEnd").GetString()=="18:30","Configured working hours must survive preference normalization and serialization.");
        using var legacy=JsonDocument.Parse(JsonSerializer.Serialize(Parse("{}")));
        Check(legacy.RootElement.GetProperty("WorkStart").GetString()=="08:30"&&legacy.RootElement.GetProperty("WorkEnd").GetString()=="17:50","Legacy preferences must default to 08:30–17:50.");
        using var invalid=JsonDocument.Parse(JsonSerializer.Serialize(Parse("{\"WorkStart\":\"25:00\",\"WorkEnd\":\"07:00\"}")));
        Check(invalid.RootElement.GetProperty("WorkStart").GetString()=="08:30"&&invalid.RootElement.GetProperty("WorkEnd").GetString()=="17:50","Invalid work hours must fall back to a valid pair.");
        Check(!Preferences.TryWorkHours("18:00","08:00",out _,out _)&&Preferences.TryWorkHours("09:00","18:00",out _,out _),"The settings form must accept valid same-day work hours and reject reversed ranges.");
        var fraction=typeof(TaskbarClockWindow).GetMethod("DayProgress",All)!;
        double At(int hour,int minute,int second=0)=>(double)fraction.Invoke(null,new object[]{new DateTime(2026,11,27,hour,minute,second)})!;
        Check(At(0,0)==0&&At(12,0)==.5&&At(23,59)==1439/1440.0,"The day background must represent 00:00–24:00 rather than elapsed work hours.");
        Check(At(13,24,0)==At(13,24,59),"Seconds must not advance the background within the same minute.");
        var window=new TaskbarClockWindow();
        try
        {
            var update=typeof(TaskbarClockWindow).GetMethod("UpdateTime",All)!;
            var apply=typeof(TaskbarClockWindow).GetMethod("ApplyAppearance",All)!;
            var progress=(Border)typeof(TaskbarClockWindow).GetField("dayProgress",All)!.GetValue(window)!;
            var clip=(RectangleGeometry)progress.Clip;
            var content=(FrameworkElement)window.Content;
            content.Measure(new Size(220,72));content.Arrange(new Rect(0,0,220,72));content.UpdateLayout();
            void Time(int hour,int minute,int second=0)=>update.Invoke(window,new object[]{new DateTime(2026,11,27,hour,minute,second)});
            Time(13,24);double width=clip.Rect.Width;Time(13,24,59);
            Check(clip.Rect.Width==width&&Math.Abs(width-220*(13*60+24)/1440.0)<.001,"The actual clipped background must follow minute progress without a per-second repaint.");
            Time(13,25);Check(clip.Rect.Width>width,"The next minute must advance the visible fill.");
            Time(0,0);Check(clip.Rect.Width==0,"Midnight must reset the day background.");
            apply.Invoke(window,new object[]{new Preferences(10,24,false,49)});Time(0,0);
            var unfilledMorning=Pixel(content,.20,.18);var unfilledWork=Pixel(content,.45,.18);var unfilledFuture=Pixel(content,.85,.18);
            Time(6,0);var earlyFill=Pixel(content,.20,.18);Time(13,24);
            var elapsed=Pixel(content,.20,.18);var workFill=Pixel(content,.45,.18);var future=Pixel(content,.85,.18);
            int Difference(Color one,Color two)=>Math.Max(Math.Abs(one.R-two.R),Math.Max(Math.Abs(one.G-two.G),Math.Abs(one.B-two.B)));
            int Chroma(Color color)=>Math.Max(color.R,Math.Max(color.G,color.B))-Math.Min(color.R,Math.Min(color.G,color.B));
            Check(Difference(elapsed,unfilledMorning)>=24&&Chroma(elapsed)>=24,"Elapsed time on the actual neutral glass theme must have a visibly colored fill, not a nearly invisible shading overlay.");
            Check(Difference(workFill,unfilledWork)>=24&&Chroma(workFill)>=24,"Elapsed working hours must retain a distinct colored fill.");
            Check(future==unfilledFuture,"Future time must keep the original glass background exactly.");
            Check(earlyFill==elapsed,"Entering work hours must not recolor the already elapsed non-work segment.");
            Check(GlassTheme.Luminance(workFill)>GlassTheme.Luminance(elapsed),"Only the work-range portion of elapsed progress must use the brighter green.");
            var earlyGradient=Pixel(content,.10,.18);var laterGradient=Pixel(content,.30,.18);
            Check(laterGradient.R>earlyGradient.R+5,"The elapsed green-to-lime fill must progress continuously before work hours rather than remain a flat color block.");
            foreach(string tint in new[]{"#1C2072","#F7F9F9","#242B2D","#E83845","#85E39C","#FFFF00"})
            {
                var p=Parse("{\"TaskbarColor\":\""+tint+"\",\"Opacity\":35,\"WorkStart\":\"08:30\",\"WorkEnd\":\"17:50\"}");
                apply.Invoke(window,new object[]{p});Time(23,59);
                var morning=Pixel(content,.20,.18);var working=Pixel(content,.55,.18);
                Check(GlassTheme.Luminance(working)>GlassTheme.Luminance(morning),"Work hours must render brighter than non-work hours in both light and dark themes.");
                var ink=((SolidColorBrush)((TextBlock)typeof(TaskbarClockWindow).GetField("pastClock",All)!.GetValue(window)!).Foreground).Color;
                foreach(var background in new[]{morning,working})
                {
                    double one=GlassTheme.Luminance(ink),two=GlassTheme.Luminance(background);
                    Check((Math.Max(one,two)+.05)/(Math.Min(one,two)+.05)>=4.5,"The colored progress fill must preserve readable clock text across custom theme colors.");
                }
                var moved=Parse("{\"TaskbarColor\":\""+tint+"\",\"Opacity\":35,\"WorkStart\":\"14:00\",\"WorkEnd\":\"16:00\"}");
                apply.Invoke(window,new object[]{moved});
                Check(GlassTheme.Luminance(Pixel(content,.55,.18))<GlassTheme.Luminance(working),"Changing configured work hours must move the actual brighter band.");
            }
            Check(!window.IsVisible&&!window.IsHitTestVisible,"Clock rendering checks must stay hidden and preserve click-through behavior.");
            if(previewPath is not null)
            {
                var scene=new DrawingVisual();using(var drawing=scene.RenderOpen())
                {
                    drawing.DrawRectangle(new LinearGradientBrush(Color.FromRgb(7,21,72),Color.FromRgb(8,65,69),30),null,new Rect(0,0,220,72));
                    drawing.DrawEllipse(new SolidColorBrush(Color.FromRgb(100,170,176)),null,new Point(135,26),24,20);
                    drawing.DrawEllipse(new SolidColorBrush(Color.FromRgb(33,72,141)),null,new Point(40,52),40,20);
                }
                var backdrop=new RenderTargetBitmap(440,144,192,192,PixelFormats.Pbgra32);backdrop.Render(scene);backdrop.Freeze();
                ((Image)typeof(TaskbarClockWindow).GetField("sample",All)!.GetValue(window)!).Source=backdrop;
                apply.Invoke(window,new object[]{new Preferences(10,24,false,49){TaskbarColor="#1C2072"}});Time(13,24);
                content.UpdateLayout();var bitmap=new RenderTargetBitmap(660,216,288,288,PixelFormats.Pbgra32);bitmap.Render(content);
                var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file=System.IO.File.Create(previewPath);encoder.Save(file);
            }
        }
        finally{window.Close();}
        Console.WriteLine("PASS: bright green elapsed progress, untouched future glass, unchanged earlier segments, brighter configured work range, adaptive text contrast, persisted hours, minute updates and midnight reset.");
    }
    private static Color Pixel(FrameworkElement content,double x,double y)
    {
        content.UpdateLayout();var bitmap=new RenderTargetBitmap(220,72,96,96,PixelFormats.Pbgra32);bitmap.Render(content);
        var pixel=new byte[4];bitmap.CopyPixels(new Int32Rect((int)(220*x),(int)(72*y),1,1),pixel,4,0);
        return Color.FromRgb(pixel[2],pixel[1],pixel[0]);
    }
}
