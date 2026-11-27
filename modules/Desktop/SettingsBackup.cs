using System;
using System.IO;
using System.Text.Json;

namespace TaskGlass;

internal static class SettingsBackup
{
    internal sealed record Configuration(Preferences Preferences,string Launcher);
    internal static string Serialize(Preferences preferences,string launcher)
    {
        LauncherData.Parse(launcher);
        return JsonSerializer.Serialize(new { Format="PathDeck",Version=1,Preferences=preferences,Launcher=JsonSerializer.Deserialize<JsonElement>(launcher) },new JsonSerializerOptions {WriteIndented=true});
    }
    internal static Configuration Parse(string json)
    {
        if(json.Length>4*1024*1024)throw new ArgumentException("配置文件超过 4 MB 限制。");
        using var document=JsonDocument.Parse(json);var root=document.RootElement;
        if(!root.TryGetProperty("Format",out var format)||format.GetString() is not ("PathDeck" or "TaskGlass")||!root.TryGetProperty("Version",out var version)||version.GetInt32()!=1)
            throw new ArgumentException("这不是受支持的 PathDeck 配置备份。");
        var preferences=Preferences.Normalize(root.GetProperty("Preferences").Deserialize<Preferences>()??throw new ArgumentException("备份缺少设置。"));
        string launcher=root.GetProperty("Launcher").GetRawText();LauncherController.ValidateImport(launcher);
        return new Configuration(preferences,launcher);
    }
    internal static void Save(string path,string json)
    {string temporary=path+".new";File.WriteAllText(temporary,json);File.Move(temporary,path,true);}
}
