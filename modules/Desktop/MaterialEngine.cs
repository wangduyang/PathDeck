using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace TaskGlass;

internal sealed class MaterialEngine : IDisposable
{
    private Process? process;
    private readonly string folder = Path.Combine(Preferences.Root, "Engine");
    internal bool Running => process is { HasExited: false };

    internal void Configure(Preferences settings)
    {
        string color = $"{settings.TaskbarColor}{(int)Math.Round(settings.Opacity * 255 / 100):X2}";
        var normal = new { enabled = false, accent = "normal" };
        var config = new
        {
            desktop_appearance = new { accent = settings.Blur > 0 ? "blur" : "clear", color, show_peek = true, show_line = false, blur_radius = settings.Blur * .6 },
            visible_window_appearance = normal, maximized_window_appearance = normal,
            start_opened_appearance = normal, search_opened_appearance = normal,
            task_view_opened_appearance = normal, battery_saver_appearance = normal,
            hide_tray = true, disable_saving = true
        };
        string path = Path.Combine(folder, "settings.json");
        string temporary = path + ".new";
        File.WriteAllText(temporary, JsonSerializer.Serialize(config));
        File.Move(temporary, path, true);
    }

    internal void Start(Preferences settings)
    {
        if (Running) return;
        if (Process.GetProcessesByName("TranslucentTB").Any(p => !p.HasExited))
            throw new InvalidOperationException("已有 TranslucentTB 正在运行，请先退出它，避免两个材质控制器同时生效。");
        Configure(settings);
        process = Process.Start(new ProcessStartInfo(Path.Combine(folder, "TranslucentTB.exe"))
        { WorkingDirectory = folder, UseShellExecute = false, CreateNoWindow = true });
        if (process is null) throw new InvalidOperationException("无法启动任务栏材质引擎。");
    }

    public void Dispose()
    {
        if (process is null) return;
        if (!process.HasExited)
        {
            Native.CloseEngine((uint)process.Id);
            if (!process.WaitForExit(4000)) process.Kill();
        }
        process.Dispose(); process = null;
    }
}
