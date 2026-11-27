using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace TaskGlass;

internal sealed class LaunchEntry
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Target { get; set; } = "";
    public string Kind { get; set; } = "Path";
    [JsonIgnore] public ImageSource? Icon { get; set; }
    internal static void ValidateManualTarget(string target,string kind,bool requireExists)
    {
        if(string.IsNullOrWhiteSpace(target)||target.Length>8192||target.Any(char.IsControl))throw new ArgumentException("请输入有效的快捷方式、文件夹或 HTTP/S 网址。");
        if(kind=="Url")
        {
            if(!Uri.TryCreate(target,UriKind.Absolute,out var uri)||!SearchEngine.IsWebUri(uri)||!string.IsNullOrEmpty(uri.UserInfo))throw new ArgumentException("网址只支持 HTTP 或 HTTPS。");
            return;
        }
        if((kind!="Path"&&kind!="Folder")||!Path.IsPathFullyQualified(target)||target.IndexOf('"')>=0)throw new ArgumentException("请选择完整路径的快捷方式或文件夹。");
        if(kind!="Folder"&&!Directory.Exists(target) && !new[]{".lnk",".url",".exe"}.Contains(Path.GetExtension(target),StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("请选择快捷方式（.lnk、.url）、应用（.exe）或现有文件夹。");
        if(requireExists&&((kind=="Folder"&&!Directory.Exists(target))||(!File.Exists(target)&&!Directory.Exists(target))))throw new ArgumentException("项目已被移动或删除。请重新添加，或从启动台移除此项目。");
    }
    internal void Launch()
    {
        if(Kind=="App")
        {
            if(string.IsNullOrWhiteSpace(Target)||Target.Any(char.IsControl)||Target.IndexOfAny(new[]{'"','\\','/'})>=0||!Target.Contains('!'))
                throw new InvalidOperationException("应用标识已失效。请从启动台移除后刷新应用。");
            if(!LaunchCatalog.IsPackagedAppInstalled(Target))throw new InvalidOperationException("此应用已卸载或不再可用。请从启动台移除后刷新应用。");
            var info=new ProcessStartInfo("explorer.exe") {UseShellExecute=false};info.ArgumentList.Add("shell:AppsFolder\\"+Target);Process.Start(info);return;
        }
        ValidateManualTarget(Target,Kind,true);
        Process.Start(new ProcessStartInfo(Target) {UseShellExecute=true});
    }
}

internal sealed record InstalledBrowser(string Name,string Path);

internal static class LaunchCatalog
{
    [StructLayout(LayoutKind.Sequential)] private struct IconSize { public int Width,Height; }
    [ComImport,Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(IconSize size,uint flags,out nint bitmap);
    }
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)] private static extern int SHCreateItemFromParsingName(string name,nint context,in Guid iid,[MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory item);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint handle);
    internal static Task<IReadOnlyList<LaunchEntry>> FetchAsync(CancellationToken token,IReadOnlyList<LaunchEntry>? manualEntries=null,IProgress<IReadOnlyList<LaunchEntry>>? progress=null)
    {
        var completion=new TaskCompletionSource<IReadOnlyList<LaunchEntry>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>
        {
            try {var entries=Fetch(token,progress);if(manualEntries is not null)foreach(var entry in manualEntries){token.ThrowIfCancellationRequested();if(entry.Kind=="Path"||entry.Kind=="Folder")entry.Icon=ReadIcon(entry.Target);}completion.TrySetResult(entries);}
            catch(OperationCanceledException){completion.TrySetCanceled(token);}
            catch(Exception error){completion.TrySetException(error);}
        }) {IsBackground=true,Name="TaskGlass app catalog"};
        thread.SetApartmentState(ApartmentState.STA);thread.Start();return completion.Task;
    }
    private static IReadOnlyList<LaunchEntry> Fetch(CancellationToken token,IProgress<IReadOnlyList<LaunchEntry>>? progress)
    {
        var entries=new Dictionary<string,LaunchEntry>(StringComparer.OrdinalIgnoreCase);
        // Shell enumeration avoids following directory junctions or moving any Start menu files.
        foreach(string root in new[]{Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),Environment.GetFolderPath(Environment.SpecialFolder.Programs)})
        {
            if(!Directory.Exists(root))continue;
            var pending=new Stack<string>();pending.Push(root);
            while(pending.Count>0)
            {
                token.ThrowIfCancellationRequested();string folder=pending.Pop();
                try
                {
                    foreach(string child in Directory.EnumerateDirectories(folder))if((File.GetAttributes(child)&FileAttributes.ReparsePoint)==0)pending.Push(child);
                    foreach(string file in Directory.EnumerateFiles(folder))
                    {
                        token.ThrowIfCancellationRequested();
                        if(!new[]{".lnk",".url"}.Contains(Path.GetExtension(file),StringComparer.OrdinalIgnoreCase))continue;
                        string relative=Path.GetRelativePath(root,file);string id="start:"+relative.ToLowerInvariant();
                        entries[id]=new LaunchEntry {Id=id,Name=Path.GetFileNameWithoutExtension(file),Target=file,Icon=ReadIcon(file)};
                        if(entries.Count%16==0)progress?.Report(entries.Values.OrderBy(e=>e.Name,StringComparer.CurrentCultureIgnoreCase).ToArray());
                    }
                }
                catch(UnauthorizedAccessException) { }
                catch(IOException) { }
            }
        }
        object? shell=null,folderObject=null,itemsObject=null;
        try
        {
            var shellType=Type.GetTypeFromProgID("Shell.Application");
            if(shellType is not null)
            {
                shell=Activator.CreateInstance(shellType);dynamic automation=shell!;folderObject=automation.NameSpace("shell:AppsFolder");
                if(folderObject is not null)
                {
                    dynamic folder=folderObject;itemsObject=folder.Items();dynamic items=itemsObject;
                    int count=(int)items.Count;
                    for(int i=0;i<count;i++)
                    {
                        token.ThrowIfCancellationRequested();object? itemObject=null;
                        try
                        {
                            itemObject=items.Item(i);dynamic item=itemObject;
                            string id=Convert.ToString(item.ExtendedProperty("System.AppUserModel.ID"))??"";
                            if(!id.Contains('!'))continue; // Classic desktop apps already have their original Start shortcut.
                            string name=Convert.ToString(item.Name)??id;
                            entries["app:"+id]=new LaunchEntry {Id="app:"+id,Name=name,Target=id,Kind="App",Icon=ReadIcon("shell:AppsFolder\\"+id)};
                            if(entries.Count%16==0)progress?.Report(entries.Values.OrderBy(e=>e.Name,StringComparer.CurrentCultureIgnoreCase).ToArray());
                        }
                        catch(COMException) { }
                        finally {Release(itemObject);}
                    }
                }
            }
        }
        catch(COMException) { }
        finally {Release(itemsObject);Release(folderObject);Release(shell);}
        return entries.Values.OrderBy(e=>e.Name,StringComparer.CurrentCultureIgnoreCase).ToArray();
    }
    private static void Release(object? value) {if(value is not null&&Marshal.IsComObject(value))Marshal.FinalReleaseComObject(value);}
    internal static ImageSource? ReadIcon(string target)
    {
        IShellItemImageFactory? factory=null;nint bitmap=0;
        try
        {
            Guid iid=typeof(IShellItemImageFactory).GUID;
            if(SHCreateItemFromParsingName(target,0,in iid,out factory)<0||factory.GetImage(new IconSize{Width=64,Height=64},4,out bitmap)<0||bitmap==0)return null;
            var image=Imaging.CreateBitmapSourceFromHBitmap(bitmap,0,Int32Rect.Empty,System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());image.Freeze();return image;
        }
        catch(COMException){return null;}
        catch(ArgumentException){return null;}
        finally {if(bitmap!=0)DeleteObject(bitmap);Release(factory);}
    }
    internal static bool IsPackagedAppInstalled(string id)
    {
        object? shell=null,folderObject=null,itemObject=null;
        try
        {
            var type=Type.GetTypeFromProgID("Shell.Application");if(type is null)return false;
            shell=Activator.CreateInstance(type);dynamic automation=shell!;folderObject=automation.NameSpace("shell:AppsFolder");
            if(folderObject is null)return false;dynamic folder=folderObject;itemObject=folder.ParseName(id);return itemObject is not null;
        }
        catch(COMException){return false;}
        finally {Release(itemObject);Release(folderObject);Release(shell);}
    }
    internal static IReadOnlyList<InstalledBrowser> InstalledBrowsers()
    {
        var browsers=new Dictionary<string,InstalledBrowser>(StringComparer.OrdinalIgnoreCase);
        foreach(var root in new[]{Registry.LocalMachine,Registry.CurrentUser})
        {
            using(var clients=root.OpenSubKey(@"SOFTWARE\Clients\StartMenuInternet"))
            {
                if(clients is not null)foreach(string keyName in clients.GetSubKeyNames())
                {
                    using var key=clients.OpenSubKey(keyName);using var command=key?.OpenSubKey(@"shell\open\command");
                    string raw=Convert.ToString(command?.GetValue(null))??"";string path="";
                    if(raw.StartsWith('"')) {int end=raw.IndexOf('"',1);if(end>0)path=raw.Substring(1,end-1);}
                    else {int end=raw.IndexOf(".exe",StringComparison.OrdinalIgnoreCase);if(end>=0)path=raw.Substring(0,end+4);}
                    if(Path.IsPathFullyQualified(path)&&File.Exists(path))browsers[path]=new InstalledBrowser(Convert.ToString(key?.GetValue(null))??keyName,path);
                }
            }
            foreach(string exe in new[]{"msedge.exe","chrome.exe","firefox.exe","brave.exe","opera.exe"})
            {
                using var key=root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\"+exe);string path=Convert.ToString(key?.GetValue(null))?.Trim('"')??"";
                if(Path.IsPathFullyQualified(path)&&File.Exists(path)&&!browsers.ContainsKey(path))browsers[path]=new InstalledBrowser(Path.GetFileNameWithoutExtension(exe),path);
            }
        }
        return browsers.Values.OrderBy(b=>b.Name,StringComparer.CurrentCultureIgnoreCase).ToArray();
    }
    internal static void LaunchSearch(SearchEngine engine,string query)
    {
        string url=engine.BuildUri(query).AbsoluteUri;
        if(string.IsNullOrEmpty(engine.BrowserPath)) {Process.Start(new ProcessStartInfo(url){UseShellExecute=true});return;}
        if(!InstalledBrowsers().Any(b=>string.Equals(b.Path,engine.BrowserPath,StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("此搜索引擎所选浏览器已卸载。请在“搜索引擎”中改用系统默认浏览器。");
        var info=new ProcessStartInfo(engine.BrowserPath){UseShellExecute=false};info.ArgumentList.Add(url);Process.Start(info);
    }
}




