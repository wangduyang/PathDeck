using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace TaskGlass;

internal sealed class LauncherController : IDisposable
{
    private readonly Func<Preferences> getPreferences;
    private readonly Action<string> report;
    private readonly string path=Path.Combine(Preferences.Root,"Data","launcher.json");
    private LauncherData data;
    private LauncherWindow? window;
    private IReadOnlyList<LaunchEntry> catalog=Array.Empty<LaunchEntry>();
    private CancellationTokenSource? catalogCancellation;
    private bool loaded,disposed;
    internal event Action? Changed;
    internal bool IsOpen => window?.IsVisible==true;
    internal int AppCount => catalog.Concat(data.ManualEntries).Count(e=>!data.HiddenApps.Contains(e.Id,StringComparer.OrdinalIgnoreCase));
    internal LauncherController(Func<Preferences> getPreferences,Action<string> report)
    {
        this.getPreferences=getPreferences;this.report=report;data=LauncherData.CreateDefaults();
        if(File.Exists(path))
            try {data=LauncherData.Parse(File.ReadAllText(path));}
            catch(Exception error) when(error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
            {report("启动台数据未能读取，原文件已保留。"+error.Message);}
    }
    internal void Show()
    {
        if(disposed||!getPreferences().LauncherEnabled)return;
        if(window is null)
        {
            window=new LauncherWindow(this,data);
            window.IsVisibleChanged+=(_,_)=>Changed?.Invoke();
        }
        if(window.IsVisible&&!window.IsClosing){window.Dismiss();return;}
        window.Open(getPreferences());window.SetCatalog(catalog,!loaded);
        if(!loaded&&catalogCancellation is null)RefreshCatalog();
    }
    internal void Hide() {window?.Dismiss();}
    internal void Update(Preferences preferences) {if(disposed)return;if(!preferences.LauncherEnabled)window?.Dismiss();else window?.Configure(preferences);}
    internal void SaveData() {data.Save(path);Changed?.Invoke();}
    internal async void RefreshCatalog()
    {
        if(disposed)return;
        catalogCancellation?.Cancel();catalogCancellation?.Dispose();
        var cancellation=new CancellationTokenSource();catalogCancellation=cancellation;
        window?.SetCatalog(catalog,true);
        try
        {
            var progress=new Progress<IReadOnlyList<LaunchEntry>>(entries=>
            {
                if(disposed||cancellation.IsCancellationRequested||!ReferenceEquals(catalogCancellation,cancellation))return;
                catalog=entries;window?.SetCatalog(catalog,true);Changed?.Invoke();
            });
            var entries=await LaunchCatalog.FetchAsync(cancellation.Token,data.ManualEntries.ToArray(),progress);
            if(disposed||cancellation.IsCancellationRequested)return;
            catalog=entries;loaded=true;window?.SetCatalog(catalog,false);Changed?.Invoke();
        }
        catch(OperationCanceledException) { }
        catch(Exception error)
        {
            if(!disposed&&!cancellation.IsCancellationRequested){loaded=true;window?.SetCatalog(catalog,false,"无法读取部分本机应用："+error.Message);report("启动台应用读取失败："+error.Message);}
        }
        finally
        {
            if(ReferenceEquals(catalogCancellation,cancellation)){catalogCancellation=null;cancellation.Dispose();}
        }
    }
    internal string ExportData() => data.ToJson();
    internal static void ValidateImport(string json)
    {
        var candidate=LauncherData.Parse(json);ValidateBrowsers(candidate);
    }
    private static void ValidateBrowsers(LauncherData candidate)
    {
        var selected=candidate.Engines.Where(e=>!string.IsNullOrEmpty(e.BrowserPath)).ToArray();if(selected.Length==0)return;
        var installed=LaunchCatalog.InstalledBrowsers();
        if(selected.Any(e=>!installed.Any(b=>string.Equals(b.Path,e.BrowserPath,StringComparison.OrdinalIgnoreCase))))
            throw new ArgumentException("启动台数据引用了本机未安装的浏览器。请先将对应引擎改为系统默认浏览器后再导入。");
    }
    internal void ImportData(string json)
    {
        var candidate=LauncherData.Parse(json);ValidateBrowsers(candidate);candidate.Save(path);
        catalogCancellation?.Cancel();catalogCancellation?.Dispose();catalogCancellation=null;loaded=false;
        data=candidate;window?.SetData(data);Changed?.Invoke();if(window?.IsVisible==true)RefreshCatalog();
    }
    public void Dispose()
    {
        if(disposed)return;disposed=true;catalogCancellation?.Cancel();catalogCancellation?.Dispose();catalogCancellation=null;window?.Close();window=null;catalog=Array.Empty<LaunchEntry>();
    }
}


