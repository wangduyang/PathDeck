using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TaskGlass;

internal sealed class LauncherCategory
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
}

internal sealed class SearchEngine
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Template { get; set; } = "";
    public string? BrowserPath { get; set; }
    internal static void ValidateTemplate(string template)
    {
        if (string.IsNullOrWhiteSpace(template) || template.Length > 2048 || template.Count(c => c == '\r' || c == '\n') != 0 ||
            template.IndexOf("%s", StringComparison.Ordinal) < 0 || template.IndexOf("%s", StringComparison.Ordinal) != template.LastIndexOf("%s", StringComparison.Ordinal))
            throw new ArgumentException("搜索地址必须包含且仅包含一个 %s。例：https://www.baidu.com/s?wd=%s");
        string candidate = template.Replace("%s", "taskglass-query", StringComparison.Ordinal);
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) || !IsWebUri(uri) || !string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException("搜索地址只支持不带账号密码的 HTTP 或 HTTPS 网址。");
        int authorityEnd = template.IndexOfAny(new[] { '/', '?', '#' }, template.IndexOf("://", StringComparison.Ordinal) + 3);
        if (authorityEnd < 0 || template.IndexOf("%s", StringComparison.Ordinal) < authorityEnd)
            throw new ArgumentException("%s 必须放在网址的路径或查询参数中，不能放在域名中。");
    }
    internal Uri BuildUri(string query)
    {
        ValidateTemplate(Template);
        return new Uri(Template.Replace("%s", Uri.EscapeDataString(query), StringComparison.Ordinal), UriKind.Absolute);
    }
    internal static bool IsWebUri(Uri uri) => (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) && !string.IsNullOrWhiteSpace(uri.Host);
}

internal sealed class LauncherData
{
    public int Version { get; set; } = 1;
    public List<LauncherCategory> Categories { get; set; } = new();
    public Dictionary<string, string> Assignments { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> AppOrder { get; set; } = new();
    public List<LaunchEntry> ManualEntries { get; set; } = new();
    public List<string> HiddenApps { get; set; } = new();
    public List<SearchEngine> Engines { get; set; } = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    internal static LauncherData CreateDefaults() => new()
    {
        Engines = new List<SearchEngine>
        {
            new() { Id="baidu", Name="百度", Template="https://www.baidu.com/s?wd=%s" },
            new() { Id="bing", Name="Bing", Template="https://www.bing.com/search?q=%s" },
            new() { Id="google", Name="Google", Template="https://www.google.com/search?q=%s" },
            new() { Id="bilibili", Name="哔哩哔哩", Template="https://search.bilibili.com/all?keyword=%s" },
            new() { Id="github", Name="GitHub", Template="https://github.com/search?q=%s" },
            new() { Id="youtube", Name="YouTube", Template="https://www.youtube.com/results?search_query=%s" }
        }
    };
    internal string AddCategory(string name)
    {
        ValidateName(name, "分类");
        if (Categories.Any(c => string.Equals(c.Name,name.Trim(),StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("已有同名分类。");
        var category = new LauncherCategory { Name = name.Trim() }; Categories.Add(category); return category.Id;
    }
    internal void RenameCategory(string id, string name)
    {
        ValidateName(name,"分类");
        if (Categories.Any(c => c.Id != id && string.Equals(c.Name,name.Trim(),StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("已有同名分类。");
        Categories.Single(c => c.Id == id).Name = name.Trim();
    }
    internal void RemoveCategory(string id)
    {
        Categories.RemoveAll(c => c.Id == id);
        foreach (string app in Assignments.Where(p => p.Value == id).Select(p => p.Key).ToArray()) Assignments.Remove(app);
    }
    internal void MoveCategory(string id, int direction)
    {
        int index=Categories.FindIndex(c => c.Id==id); if(index<0)return;
        int next=Math.Clamp(index+direction,0,Categories.Count-1); var item=Categories[index];Categories.RemoveAt(index);Categories.Insert(next,item);
    }
    internal string CategoryOf(string app) => Assignments.TryGetValue(app,out string? category) ? category : "";
    internal void Assign(string app, string category)
    {
        if (category.Length > 0 && !Categories.Any(c => c.Id == category)) throw new ArgumentException("分类已不存在。");
        if(category.Length==0) Assignments.Remove(app); else Assignments[app]=category;
        if(!AppOrder.Contains(app,StringComparer.OrdinalIgnoreCase)) AppOrder.Add(app);
    }
    internal void MoveApp(string app, string before)
    {
        if(string.Equals(app,before,StringComparison.OrdinalIgnoreCase))return;
        if(!AppOrder.Contains(before,StringComparer.OrdinalIgnoreCase))AppOrder.Add(before);
        AppOrder.RemoveAll(x=>string.Equals(x,app,StringComparison.OrdinalIgnoreCase));
        AppOrder.Insert(AppOrder.FindIndex(x=>string.Equals(x,before,StringComparison.OrdinalIgnoreCase)),app);
    }
    internal void RemoveApp(string id)
    {
        if(ManualEntries.RemoveAll(x=>x.Id==id)==0 && !HiddenApps.Contains(id,StringComparer.OrdinalIgnoreCase))HiddenApps.Add(id);
        Assignments.Remove(id);AppOrder.RemoveAll(x=>string.Equals(x,id,StringComparison.OrdinalIgnoreCase));
    }
    internal string ToJson() { Validate(); return JsonSerializer.Serialize(this,JsonOptions); }
    internal static LauncherData Parse(string json)
    {
        if(string.IsNullOrWhiteSpace(json)||json.Length>2_000_000)throw new ArgumentException("启动台数据为空或过大。");
        using var document=JsonDocument.Parse(json);
        if(document.RootElement.ValueKind!=JsonValueKind.Object || !document.RootElement.TryGetProperty("Version",out var version) || !version.TryGetInt32(out int value) || value!=1)
            throw new ArgumentException("不支持的启动台数据版本。");
        var result=JsonSerializer.Deserialize<LauncherData>(json,JsonOptions) ?? throw new ArgumentException("启动台数据无效。");
        result.Validate();result.Assignments=new Dictionary<string,string>(result.Assignments,StringComparer.OrdinalIgnoreCase);return result;
    }
    private void Validate()
    {
        if(Version!=1||Categories is null||Assignments is null||AppOrder is null||ManualEntries is null||HiddenApps is null||Engines is null)
            throw new ArgumentException("启动台数据结构不完整。");
        if(Categories.Count>100||Engines.Count>100||ManualEntries.Count>5000||AppOrder.Count>20000||Assignments.Count>20000||HiddenApps.Count>20000)
            throw new ArgumentException("启动台数据超出容量限制。");
        var categoryIds=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var category in Categories)
        {
            if(category is null)throw new ArgumentException("分类数据无效。");
            ValidateName(category.Name,"分类"); ValidateId(category.Id);
            if(!categoryIds.Add(category.Id))throw new ArgumentException("分类标识重复。");
        }
        if(Categories.Select(c=>c.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=Categories.Count)throw new ArgumentException("分类名称重复。");
        foreach(var pair in Assignments) { ValidateId(pair.Key); if(!categoryIds.Contains(pair.Value))throw new ArgumentException("应用引用了不存在的分类。"); }
        foreach(string id in AppOrder.Concat(HiddenApps))ValidateId(id);
        if(AppOrder.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=AppOrder.Count)throw new ArgumentException("应用顺序含有重复项目。");
        var manualIds=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var entry in ManualEntries)
        {
            if(entry is null)throw new ArgumentException("启动项目无效。");
            ValidateName(entry.Name,"应用");ValidateId(entry.Id);
            if(!manualIds.Add(entry.Id)||!entry.Id.StartsWith("manual:",StringComparison.Ordinal))throw new ArgumentException("手动项目标识无效或重复。");
            LaunchEntry.ValidateManualTarget(entry.Target,entry.Kind,false);
        }
        var engineIds=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var engine in Engines)
        {
            if(engine is null)throw new ArgumentException("搜索引擎无效。");
            ValidateId(engine.Id);ValidateName(engine.Name,"搜索引擎");SearchEngine.ValidateTemplate(engine.Template);
            if(!engineIds.Add(engine.Id))throw new ArgumentException("搜索引擎标识重复。");
            if(!string.IsNullOrEmpty(engine.BrowserPath) && (!Path.IsPathFullyQualified(engine.BrowserPath) || !string.Equals(Path.GetExtension(engine.BrowserPath),".exe",StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("浏览器必须是已安装浏览器的完整 EXE 路径。");
        }
    }
    private static void ValidateId(string id) { if(string.IsNullOrWhiteSpace(id)||id.Length>2048||id.Any(char.IsControl))throw new ArgumentException("项目标识无效。"); }
    internal static void ValidateName(string name,string label)
    {
        if(string.IsNullOrWhiteSpace(name)||name.Trim().Length>80||name.Any(char.IsControl))throw new ArgumentException(label+"名称应为 1–80 个可见字符。");
    }
    internal void Save(string path)
    {
        string json=ToJson();string? folder=Path.GetDirectoryName(path);if(folder is not null)Directory.CreateDirectory(folder);
        string temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try {File.WriteAllText(temporary,json);File.Move(temporary,path,true);}
        finally {if(File.Exists(temporary))File.Delete(temporary);}
    }
}
