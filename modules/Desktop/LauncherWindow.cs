using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace TaskGlass;

internal sealed class LauncherWindow : GlassPanel
{
    private readonly LauncherController controller;
    private LauncherData data;
    private IReadOnlyList<LaunchEntry> catalog = Array.Empty<LaunchEntry>();
    private readonly TextBox search = new() { Name="LauncherSearch",FontSize=18,MinHeight=52,Padding=new Thickness(16,10,45,10),VerticalContentAlignment=VerticalAlignment.Center };
    private readonly WrapPanel engines = new() { Orientation=Orientation.Horizontal };
    private readonly StackPanel categories = new();
    private readonly WrapPanel apps = new();
    private readonly TextBlock count = new() { FontSize=12,Margin=new Thickness(3,8,0,10),Opacity=.7 };
    private readonly TextBlock status = new() { FontSize=12,TextWrapping=TextWrapping.Wrap,Opacity=.8,Margin=new Thickness(0,10,0,0) };
    private readonly TextBlock empty = new() { Text="正在读取本机应用…",FontSize=16,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(26),VerticalAlignment=VerticalAlignment.Center,HorizontalAlignment=HorizontalAlignment.Center };
    private readonly Grid sheetHost = new() { Visibility=Visibility.Collapsed };
    private readonly ScrollViewer appScroll;
    private readonly TranslateTransform categoryShift = new();
    private DateTime categoryMotionUntil;
    private readonly DispatcherTimer filterTimer = new() { Interval=TimeSpan.FromMilliseconds(80) };
    private readonly List<Button> tiles = new();
    private readonly Button submit;
    private string selectedCategory="";
    private string? selectedEngine;
    private bool composing,loading=true;
    private Point dragOrigin;
    private bool dragged;
    internal LauncherWindow(LauncherController controller,LauncherData data) : base("启动台",850,650)
    {
        this.controller=controller;this.data=data;
        System.Windows.Automation.AutomationProperties.SetName(search,"搜索本机应用或网页");
        HeaderActions.Children.Add(ActionButton("添加",()=>ShowAdd(),true));
        HeaderActions.Children.Add(ActionButton("管理",ShowManage,true));
        var layout=new Grid();ContentHost.Children.Add(layout);
        layout.RowDefinitions.Add(new RowDefinition {Height=GridLength.Auto});
        layout.RowDefinitions.Add(new RowDefinition {Height=GridLength.Auto});
        layout.RowDefinitions.Add(new RowDefinition());
        layout.RowDefinitions.Add(new RowDefinition {Height=GridLength.Auto});
        var input=new Grid {Margin=new Thickness(0,10,0,8)};input.Children.Add(search);layout.Children.Add(input);
        var placeholder=new TextBlock {Text="搜索本机应用，或选择下方引擎搜索网页",Opacity=.45,Margin=new Thickness(18,0,42,0),VerticalAlignment=VerticalAlignment.Center,IsHitTestVisible=false};input.Children.Add(placeholder);
        var clear=ActionButton("×",()=>{search.Clear();search.Focus();},true);clear.Width=38;clear.MinWidth=0;clear.Height=38;clear.MinHeight=38;clear.Padding=new Thickness(0);clear.HorizontalAlignment=HorizontalAlignment.Right;clear.Margin=new Thickness(0,0,7,0);clear.ToolTip="清空搜索";input.Children.Add(clear);
        var web=new DockPanel {LastChildFill=true,Margin=new Thickness(0,0,0,12)};Grid.SetRow(web,1);layout.Children.Add(web);
        submit=ActionButton("搜索 ↗",SubmitSearch);submit.Name="LauncherWebSubmit";submit.IsEnabled=false;submit.Margin=new Thickness(8,0,0,0);DockPanel.SetDock(submit,Dock.Right);web.Children.Add(submit);web.Children.Add(new ScrollViewer {Content=engines,MaxHeight=86,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled});
        var body=new Grid();body.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(142)});body.ColumnDefinitions.Add(new ColumnDefinition());Grid.SetRow(body,2);layout.Children.Add(body);
        var categoryScroll=new ScrollViewer {Content=categories,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,Margin=new Thickness(0,0,14,0)};body.Children.Add(categoryScroll);
        var appArea=new Grid();appArea.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});appArea.RowDefinitions.Add(new RowDefinition());Grid.SetColumn(appArea,1);body.Children.Add(appArea);appArea.Children.Add(count);
        appScroll=new ScrollViewer {Content=apps,RenderTransform=categoryShift,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,CanContentScroll=false};Grid.SetRow(appScroll,1);appArea.Children.Add(appScroll);Grid.SetRow(empty,1);appArea.Children.Add(empty);
        Grid.SetRow(status,3);layout.Children.Add(status);
        ContentHost.Children.Add(sheetHost);Panel.SetZIndex(sheetHost,10);
        search.TextChanged+=(_,_)=>{placeholder.Visibility=search.Text.Length==0?Visibility.Visible:Visibility.Collapsed;clear.Visibility=search.Text.Length==0?Visibility.Collapsed:Visibility.Visible;filterTimer.Stop();filterTimer.Start();UpdateSubmit();};
        filterTimer.Tick+=(_,_)=>{filterTimer.Stop();RenderApps();};
        TextCompositionManager.AddPreviewTextInputStartHandler(search,(_,_)=>composing=true);
        TextCompositionManager.AddPreviewTextInputUpdateHandler(search,(_,_)=>composing=true);
        TextCompositionManager.AddPreviewTextInputHandler(search,(_,_)=>Dispatcher.BeginInvoke(DispatcherPriority.Input,new Action(()=>composing=false)));
        search.PreviewKeyDown+=SearchKey;
        IsVisibleChanged+=(_,_)=>{if(!IsVisible){filterTimer.Stop();composing=false;}};
        Closed+=(_,_)=>filterTimer.Stop();
        RenderCategories();RenderEngines();RenderApps();
    }
    private Button ActionButton(string label,Action action,bool quiet=false)
    {
        var button=new Button {Content=label,Margin=new Thickness(3,0,3,0),Padding=new Thickness(13,7,13,7),MinHeight=40};
        if(quiet)button.Style=(Style)FindResource("SettingsQuietButton");
        button.Click+=(_,_)=>Safe(action);return button;
    }
    private void Safe(Action action) {try {action();}catch(Exception error){status.Text=error.Message;}}
    internal void Open(Preferences preferences)
    {
        ApplyAppearance(preferences);Place(preferences);selectedEngine=null;selectedCategory="";search.Clear();CloseSheet();RenderCategories();RenderEngines();RenderApps();Present();
        Dispatcher.BeginInvoke(DispatcherPriority.Input,new Action(()=>search.Focus()));
    }
    internal void Configure(Preferences preferences)
    {
        ApplyAppearance(preferences);if(IsVisible)Place(preferences);RenderEngines();RenderCategories();
        if(!GlassMotion.Active){GlassMotion.To(categoryShift,TranslateTransform.YProperty,0,0);GlassMotion.To(appScroll,OpacityProperty,1,0);}
    }
    private void Place(Preferences preferences)
    {
        var display=DisplayGeometry.Current();var area=display.WorkingArea;double scale=DisplayGeometry.Scale(display);
        Width=preferences.LauncherFullscreen?area.Width/scale:Math.Min(850,area.Width/scale-16);
        Height=preferences.LauncherFullscreen?area.Height/scale:Math.Min(650,area.Height/scale-16);
        int width=(int)Math.Round(Width*scale),height=(int)Math.Round(Height*scale);
        Native.SetWindowPos(new WindowInteropHelper(this).EnsureHandle(),-1,area.Left+(area.Width-width)/2,area.Top+(area.Height-height)/2,width,height,0x0010);
    }
    internal void SetCatalog(IReadOnlyList<LaunchEntry> entries,bool isLoading,string? message=null)
    {
        catalog=entries;loading=isLoading;RenderCategories();RenderApps();status.Text=message??"拖动应用可排序或移入分类；右键管理。输入仅在本机筛选。";
    }
    internal void SetData(LauncherData value) {data=value;selectedCategory="";selectedEngine=null;CloseSheet();RenderCategories();RenderEngines();RenderApps();}
    private IEnumerable<LaunchEntry> Entries() => catalog.Concat(data.ManualEntries).Where(e=>!data.HiddenApps.Contains(e.Id,StringComparer.OrdinalIgnoreCase));
    private void RenderCategories()
    {
        categories.Children.Clear();
        void Add(string id,string name)
        {
            int total=Entries().Count(e=>id==""||data.CategoryOf(e.Id)==id);
            var button=ActionButton(name+"  "+total,()=>SelectCategory(id),true);
            button.Content=new TextBlock {Text=name+"  "+total,TextTrimming=TextTrimming.CharacterEllipsis};button.ToolTip=name;button.HorizontalContentAlignment=HorizontalAlignment.Left;button.Margin=new Thickness(0,2,0,2);button.Padding=new Thickness(12,8,12,8);button.AllowDrop=true;
            if(selectedCategory==id){button.Background=AccentWash();button.BorderBrush=(Brush)FindResource("SettingsAccent");button.BorderThickness=new Thickness(1);}
            button.DragOver+=(_,e)=>{e.Effects=e.Data.GetDataPresent("TaskGlass.LaunchApp")?DragDropEffects.Move:DragDropEffects.None;e.Handled=true;};
            button.Drop+=(_,e)=>{if(e.Data.GetData("TaskGlass.LaunchApp") is string app){Safe(()=>{data.Assign(app,id);controller.SaveData();RenderCategories();RenderApps();});e.Handled=true;}};
            categories.Children.Add(button);
        }
        Add("","全部应用");foreach(var category in data.Categories)Add(category.Id,category.Name);
        categories.Children.Add(ActionButton("＋ 新建分类",()=>ShowCategoryEditor(null),true));
    }
    private void SelectCategory(string id)
    {
        if(selectedCategory==id)return;
        selectedCategory=id;RenderCategories();RenderApps();appScroll.ScrollToTop();
        var now=DateTime.UtcNow;
        if(GlassMotion.Active&&now>=categoryMotionUntil)
        {
            categoryShift.BeginAnimation(TranslateTransform.YProperty,null);appScroll.BeginAnimation(OpacityProperty,null);
            categoryShift.Y=8;appScroll.Opacity=.78;
        }
        GlassMotion.To(categoryShift,TranslateTransform.YProperty,0,140);GlassMotion.To(appScroll,OpacityProperty,1,140);
        categoryMotionUntil=now.AddMilliseconds(140);
    }
    private Brush AccentWash() {var accent=(SolidColorBrush)FindResource("SettingsAccent");return new SolidColorBrush(Color.FromArgb(40,accent.Color.R,accent.Color.G,accent.Color.B));}
    private void RenderEngines()
    {
        engines.Children.Clear();
        foreach(var engine in data.Engines)
        {
            var capsule=ActionButton(engine.Name,()=>{selectedEngine=selectedEngine==engine.Id?null:engine.Id;RenderEngines();UpdateSubmit();search.Focus();},true);
            capsule.Content=new TextBlock {Text=engine.Name,MaxWidth=130,TextTrimming=TextTrimming.CharacterEllipsis};capsule.MinHeight=34;capsule.Padding=new Thickness(12,5,12,5);capsule.Margin=new Thickness(0,3,6,3);capsule.FontSize=12;capsule.ToolTip="选择 "+engine.Name+"，再按“搜索”或 Enter 提交";
            if(selectedEngine==engine.Id){capsule.Background=AccentWash();capsule.BorderBrush=(Brush)FindResource("SettingsAccent");capsule.BorderThickness=new Thickness(1);}
            engines.Children.Add(capsule);
        }
        UpdateSubmit();
    }
    private void UpdateSubmit() {if(submit is not null)submit.IsEnabled=selectedEngine is not null&&!string.IsNullOrWhiteSpace(search.Text);}
    private void SubmitSearch()
    {
        if(composing||string.IsNullOrWhiteSpace(search.Text)||selectedEngine is null)return;
        var engine=data.Engines.FirstOrDefault(e=>e.Id==selectedEngine);if(engine is null)return;
        LaunchCatalog.LaunchSearch(engine,search.Text.Trim());Dismiss();
    }
    private void RenderApps()
    {
        string? focusedId=(Keyboard.FocusedElement as Button)?.Tag is LaunchEntry focused?focused.Id:null;
        var order=new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);for(int i=0;i<data.AppOrder.Count;i++)order[data.AppOrder[i]]=i;
        string query=search.Text.Trim();
        var filtered=Entries().Where(e=>(selectedCategory==""||data.CategoryOf(e.Id)==selectedCategory)&&e.Name.Contains(query,StringComparison.OrdinalIgnoreCase))
            .OrderBy(e=>order.TryGetValue(e.Id,out int rank)?rank:int.MaxValue).ThenBy(e=>e.Name,StringComparer.CurrentCultureIgnoreCase).ToArray();
        apps.Children.Clear();tiles.Clear();count.Text=(query.Length>0?"本机匹配 · ":"本机应用 · ")+filtered.Length+(loading?"  ·  正在读取…":"");
        empty.Text=loading&&filtered.Length==0?"正在读取本机应用…":query.Length>0?"未找到匹配应用\n可选择上方搜索引擎，再提交网页搜索。":"此分类暂无应用\n拖动应用到左侧分类，或点击“添加”。";
        empty.Visibility=filtered.Length==0?Visibility.Visible:Visibility.Collapsed;
        foreach(var entry in filtered)
        {
            var content=new StackPanel {VerticalAlignment=VerticalAlignment.Center};
            if(entry.Icon is not null)content.Children.Add(new Image {Source=entry.Icon,Width=40,Height=40,Margin=new Thickness(0,2,0,8),Stretch=Stretch.Uniform});
            else content.Children.Add(new TextBlock {Text=entry.Kind=="Url"?"↗":entry.Kind=="App"?"▦":"◇",FontSize=30,HorizontalAlignment=HorizontalAlignment.Center,Height=50});
            content.Children.Add(new TextBlock {Text=entry.Name,FontSize=12,TextAlignment=TextAlignment.Center,TextWrapping=TextWrapping.Wrap,TextTrimming=TextTrimming.CharacterEllipsis,MaxHeight=34});
            var tile=new Button {Content=content,Width=108,Height=108,Padding=new Thickness(7),Margin=new Thickness(3),ToolTip=entry.Name+"\n"+entry.Target,Tag=entry,AllowDrop=true,Style=(Style)FindResource("SettingsQuietButton")};
            System.Windows.Automation.AutomationProperties.SetName(tile,entry.Name);
            tile.Click+=(_,_)=>{if(!dragged)Launch(entry);dragged=false;};
            tile.PreviewMouseRightButtonUp+=(_,e)=>{ShowApp(entry);e.Handled=true;};
            tile.PreviewMouseLeftButtonDown+=(_,e)=>{dragOrigin=e.GetPosition(tile);dragged=false;};
            tile.PreviewMouseMove+=(_,e)=>
            {
                if(e.LeftButton!=MouseButtonState.Pressed||dragged)return;var position=e.GetPosition(tile);
                if(Math.Abs(position.X-dragOrigin.X)<SystemParameters.MinimumHorizontalDragDistance&&Math.Abs(position.Y-dragOrigin.Y)<SystemParameters.MinimumVerticalDragDistance)return;
                dragged=true;var payload=new DataObject("TaskGlass.LaunchApp",entry.Id);DragDrop.DoDragDrop(tile,payload,DragDropEffects.Move);
            };
            tile.DragOver+=(_,e)=>{e.Effects=e.Data.GetDataPresent("TaskGlass.LaunchApp")?DragDropEffects.Move:DragDropEffects.None;e.Handled=true;};
            tile.Drop+=(_,e)=>{if(e.Data.GetData("TaskGlass.LaunchApp") is string moving){Safe(()=>{foreach(var ordered in tiles.Select(t=>(LaunchEntry)t.Tag))if(!data.AppOrder.Contains(ordered.Id,StringComparer.OrdinalIgnoreCase))data.AppOrder.Add(ordered.Id);data.MoveApp(moving,entry.Id);controller.SaveData();RenderApps();});e.Handled=true;}};
            tile.PreviewKeyDown+=(_,e)=>TileKey(tile,entry,e);apps.Children.Add(tile);tiles.Add(tile);
        }
        if(focusedId is not null)tiles.FirstOrDefault(t=>((LaunchEntry)t.Tag).Id==focusedId)?.Focus();
    }
    private void SearchKey(object sender,KeyEventArgs e)
    {
        if(composing||e.Key==Key.ImeProcessed||e.Key==Key.DeadCharProcessed)return;
        if(e.Key==Key.Enter) {filterTimer.Stop();RenderApps();if(selectedEngine is not null)Safe(SubmitSearch);else if(tiles.FirstOrDefault()?.Tag is LaunchEntry entry)Launch(entry);e.Handled=true;}
        else if(e.Key==Key.Down&&tiles.Count>0){tiles[0].Focus();e.Handled=true;}
    }
    private void TileKey(Button tile,LaunchEntry entry,KeyEventArgs e)
    {
        if(e.Key==Key.Apps||(e.Key==Key.F10&&(Keyboard.Modifiers&ModifierKeys.Shift)!=0)){ShowApp(entry);e.Handled=true;return;}
        int index=tiles.IndexOf(tile),columns=Math.Max(1,(int)(appScroll.ViewportWidth/114));
        int target=e.Key switch {Key.Left=>index-1,Key.Right=>index+1,Key.Up=>index-columns,Key.Down=>index+columns,_=>index};
        if(target==index)return;e.Handled=true;if(target<0){search.Focus();return;}if(target<tiles.Count){tiles[target].Focus();tiles[target].BringIntoView();}
    }
    private void Launch(LaunchEntry entry)
    {
        try {entry.Launch();Dismiss();}
        catch(Exception error){ShowApp(entry,"无法打开："+error.Message+"\n"+(entry.Id.StartsWith("manual:",StringComparison.Ordinal)?"可重新定位此项目，或从启动台移除。":"可重新读取本机应用，或从启动台移除。"));}
    }
    private StackPanel ShowSheet(string title,string? description=null)
    {
        sheetHost.Children.Clear();sheetHost.Visibility=Visibility.Visible;
        var background=new Border {CornerRadius=new CornerRadius(20),Background=(Brush)FindResource("SettingsSheetBrush"),Padding=new Thickness(20)};
        var grid=new Grid();grid.RowDefinitions.Add(new RowDefinition {Height=GridLength.Auto});grid.RowDefinitions.Add(new RowDefinition());background.Child=grid;sheetHost.Children.Add(background);
        var header=new DockPanel();var back=ActionButton("← 返回",CloseSheet,true);DockPanel.SetDock(back,Dock.Right);header.Children.Add(back);header.Children.Add(new TextBlock {Text=title,FontSize=20,FontWeight=FontWeights.SemiBold,TextTrimming=TextTrimming.CharacterEllipsis,VerticalAlignment=VerticalAlignment.Center});grid.Children.Add(header);
        var panel=new StackPanel {Margin=new Thickness(0,12,0,0)};var scroll=new ScrollViewer {Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};Grid.SetRow(scroll,1);grid.Children.Add(scroll);
        if(description is not null)panel.Children.Add(Label(description,true));
        return panel;
    }
    private void CloseSheet() {sheetHost.Visibility=Visibility.Collapsed;sheetHost.Children.Clear();search.Focus();}
    private static TextBlock Label(string text,bool muted=false)=>new() {Text=text,TextWrapping=TextWrapping.Wrap,FontSize=muted?12:14,Opacity=muted?.7:1,Margin=new Thickness(0,6,0,8)};
    private static TextBox Input(string text="")=>new() {Text=text,MinHeight=44,Padding=new Thickness(12,8,12,8),Margin=new Thickness(0,0,0,10),VerticalContentAlignment=VerticalAlignment.Center};
    private void SheetAction(StackPanel panel,string label,Action action,bool primary=false)
    {
        var button=ActionButton(label,action);button.HorizontalAlignment=HorizontalAlignment.Left;button.Margin=new Thickness(0,5,8,5);if(primary)button.Style=(Style)FindResource("SettingsPrimaryButton");panel.Children.Add(button);
    }
    private void ShowManage()
    {
        var panel=ShowSheet("管理启动台","只调整启动台内的分类和顺序，不移动或删除电脑中的原文件。");
        SheetAction(panel,"管理分类",ShowCategories);SheetAction(panel,"搜索引擎",ShowEngines);SheetAction(panel,"重新读取本机应用",()=>{CloseSheet();controller.RefreshCatalog();});
    }
    private void ShowCategories()
    {
        var panel=ShowSheet("分类","移除分类后，分类中的应用仍保留在“全部应用”中。");
        foreach(var category in data.Categories.ToArray())
        {
            var row=new DockPanel {Margin=new Thickness(0,5,0,5)};
            foreach(var (label,action) in new (string,Action)[]{("↑",()=>{data.MoveCategory(category.Id,-1);controller.SaveData();RenderCategories();ShowCategories();}),("↓",()=>{data.MoveCategory(category.Id,1);controller.SaveData();RenderCategories();ShowCategories();}),("编辑",()=>ShowCategoryEditor(category)),("移除",()=>{data.RemoveCategory(category.Id);if(selectedCategory==category.Id)selectedCategory="";controller.SaveData();RenderCategories();RenderApps();ShowCategories();})})
            {var button=ActionButton(label,action,true);DockPanel.SetDock(button,Dock.Right);row.Children.Add(button);}
            row.Children.Add(new TextBlock {Text=category.Name,VerticalAlignment=VerticalAlignment.Center,TextTrimming=TextTrimming.CharacterEllipsis});panel.Children.Add(row);
        }
        SheetAction(panel,"＋ 新建分类",()=>ShowCategoryEditor(null),true);
    }
    private void ShowCategoryEditor(LauncherCategory? category)
    {
        var panel=ShowSheet(category is null?"新建分类":"重命名分类");panel.Children.Add(Label("分类名称"));var name=Input(category?.Name??"");panel.Children.Add(name);
        var error=Label("",true);panel.Children.Add(error);
        SheetAction(panel,"保存分类",()=>{try{if(category is null)data.AddCategory(name.Text);else data.RenameCategory(category.Id,name.Text);controller.SaveData();RenderCategories();ShowCategories();}catch(Exception e){error.Text=e.Message;}},true);name.Focus();
    }
    private void ShowApp(LaunchEntry entry,string? error=null)
    {
        var panel=ShowSheet(entry.Name,error??"调整分类，或从启动台移除此项目。");
        var target=Input(entry.Target);target.IsReadOnly=true;panel.Children.Add(target);panel.Children.Add(Label("所属分类"));
        var choice=new ComboBox {MinHeight=44,Margin=new Thickness(0,0,0,10),DisplayMemberPath="Name",SelectedValuePath="Id"};
        var options=new List<LauncherCategory>{new(){Id="",Name="全部应用（不归类）"}};options.AddRange(data.Categories);choice.ItemsSource=options;choice.SelectedValue=data.CategoryOf(entry.Id);panel.Children.Add(choice);
        SheetAction(panel,"保存分类",()=>{data.Assign(entry.Id,(string?)choice.SelectedValue??"");controller.SaveData();RenderCategories();RenderApps();CloseSheet();},true);
        if(data.ManualEntries.Contains(entry))SheetAction(panel,"重新定位",()=>ShowEntryEditor(entry));
        SheetAction(panel,"打开",()=>Launch(entry));SheetAction(panel,"从启动台移除",()=>{data.RemoveApp(entry.Id);controller.SaveData();RenderCategories();RenderApps();CloseSheet();});
    }
    private void ShowAdd() => ShowEntryEditor(null);
    private void ShowEntryEditor(LaunchEntry? existing)
    {
        var panel=ShowSheet(existing is null?"添加项目":"重新定位项目",existing is null?"添加原项目的引用。支持快捷方式、应用、文件夹与 HTTP/S 网址。":"更新项目的引用，保留原分类和排序，不移动原文件。");
        panel.Children.Add(Label("显示名称"));var name=Input(existing?.Name??"");panel.Children.Add(name);panel.Children.Add(Label("路径或网址"));var target=Input(existing?.Target??"");panel.Children.Add(target);
        SheetAction(panel,"选择快捷方式 / 应用…",()=>
        {
            Present(false);try{var dialog=new OpenFileDialog {Title="选择快捷方式或应用",Filter="快捷方式与应用|*.lnk;*.url;*.exe",CheckFileExists=true};if(dialog.ShowDialog(this)==true){target.Text=dialog.FileName;if(string.IsNullOrWhiteSpace(name.Text))name.Text=System.IO.Path.GetFileNameWithoutExtension(dialog.FileName);}}finally{Present();}
        });
        SheetAction(panel,"选择文件夹…",()=>
        {
            Present(false);try{var dialog=new OpenFolderDialog {Title="选择文件夹"};if(dialog.ShowDialog(this)==true){target.Text=dialog.FolderName;if(string.IsNullOrWhiteSpace(name.Text))name.Text=System.IO.Path.GetFileName(dialog.FolderName.TrimEnd('\\'));}}finally{Present();}
        });
        var error=Label("",true);panel.Children.Add(error);
        SheetAction(panel,existing is null?"添加到启动台":"保存位置",()=>
        {
            try
            {
                string value=target.Text.Trim();string kind=Uri.TryCreate(value,UriKind.Absolute,out var uri)&&SearchEngine.IsWebUri(uri)?"Url":System.IO.Directory.Exists(value)?"Folder":"Path";
                LauncherData.ValidateName(name.Text,"项目");LaunchEntry.ValidateManualTarget(value,kind,true);
                var icon=kind!="Url"?LaunchCatalog.ReadIcon(value):null;
                if(existing is null)
                {
                    var entry=new LaunchEntry {Id="manual:"+Guid.NewGuid().ToString("N"),Name=name.Text.Trim(),Target=value,Kind=kind,Icon=icon};
                    data.ManualEntries.Add(entry);data.Assign(entry.Id,selectedCategory);controller.SaveData();
                }
                else
                {
                    string oldName=existing.Name,oldTarget=existing.Target,oldKind=existing.Kind;var oldIcon=existing.Icon;
                    existing.Name=name.Text.Trim();existing.Target=value;existing.Kind=kind;existing.Icon=icon;
                    try {controller.SaveData();}
                    catch {existing.Name=oldName;existing.Target=oldTarget;existing.Kind=oldKind;existing.Icon=oldIcon;throw;}
                }
                RenderCategories();RenderApps();CloseSheet();
            }
            catch(Exception e){error.Text=e.Message;}
        },true);name.Focus();
    }
    private void ShowEngines()
    {
        var panel=ShowSheet("搜索引擎","先选择引擎，再点击“搜索”或按 Enter 提交。输入内容不会自动发送。");
        foreach(var engine in data.Engines.ToArray())
        {
            var row=new DockPanel {Margin=new Thickness(0,5,0,5)};
            foreach(var (label,action) in new (string,Action)[]{("↑",()=>MoveEngine(engine,-1)),("↓",()=>MoveEngine(engine,1)),("编辑",()=>ShowEngineEditor(engine)),("移除",()=>{data.Engines.Remove(engine);if(selectedEngine==engine.Id)selectedEngine=null;controller.SaveData();RenderEngines();ShowEngines();})})
            {var button=ActionButton(label,action,true);DockPanel.SetDock(button,Dock.Right);row.Children.Add(button);}
            row.Children.Add(new TextBlock {Text=engine.Name,VerticalAlignment=VerticalAlignment.Center,TextTrimming=TextTrimming.CharacterEllipsis});panel.Children.Add(row);
        }
        SheetAction(panel,"＋ 添加搜索引擎",()=>ShowEngineEditor(null),true);
    }
    private void MoveEngine(SearchEngine engine,int direction)
    {
        int index=data.Engines.IndexOf(engine),next=Math.Clamp(index+direction,0,data.Engines.Count-1);data.Engines.RemoveAt(index);data.Engines.Insert(next,engine);controller.SaveData();RenderEngines();ShowEngines();
    }
    private void ShowEngineEditor(SearchEngine? engine)
    {
        var panel=ShowSheet(engine is null?"添加搜索引擎":"编辑搜索引擎","将查询位置写为 %s，例如 https://www.baidu.com/s?wd=%s。仅支持 HTTP/S。");
        panel.Children.Add(Label("名称"));var name=Input(engine?.Name??"");panel.Children.Add(name);panel.Children.Add(Label("搜索地址模板"));var template=Input(engine?.Template??"");panel.Children.Add(template);
        panel.Children.Add(Label("打开方式"));var browser=new ComboBox {MinHeight=44,Margin=new Thickness(0,0,0,10),DisplayMemberPath="Name",SelectedValuePath="Path"};
        var browsers=new List<InstalledBrowser>{new("系统默认浏览器","")};browsers.AddRange(LaunchCatalog.InstalledBrowsers());browser.ItemsSource=browsers;browser.SelectedValue=engine?.BrowserPath??"";if(browser.SelectedIndex<0)browser.SelectedIndex=0;panel.Children.Add(browser);
        var error=Label("",true);panel.Children.Add(error);
        SheetAction(panel,"保存搜索引擎",()=>
        {
            try
            {
                LauncherData.ValidateName(name.Text,"搜索引擎");SearchEngine.ValidateTemplate(template.Text.Trim());
                var value=engine??new SearchEngine();value.Name=name.Text.Trim();value.Template=template.Text.Trim();value.BrowserPath=string.IsNullOrEmpty((string?)browser.SelectedValue)?null:(string?)browser.SelectedValue;
                if(engine is null)data.Engines.Add(value);controller.SaveData();RenderEngines();ShowEngines();
            }
            catch(Exception e){error.Text=e.Message;}
        },true);name.Focus();
    }
}





