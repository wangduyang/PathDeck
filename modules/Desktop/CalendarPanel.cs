using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;
using Calendar = System.Windows.Controls.Calendar;

namespace TaskGlass;

internal sealed class CalendarPanel : Grid
{
    internal Calendar Calendar { get; }
    private readonly TextBlock selection = new() { FontSize=12,VerticalAlignment=VerticalAlignment.Center,TextWrapping=TextWrapping.Wrap };
    private readonly TranslateTransform shift = new();
    internal CalendarPanel()
    {
        Resources.MergedDictionaries.Add(new ResourceDictionary {Source=new Uri("/PathDeck.Desktop;component/CalendarStyles.xaml",UriKind.Relative)});
        Calendar=new Calendar {SelectedDate=DateTime.Today,DisplayDate=DateTime.Today,FirstDayOfWeek=DayOfWeek.Sunday,Language=XmlLanguage.GetLanguage("zh-CN"),Style=(Style)FindResource("GlassCalendar")};
        System.Windows.Automation.AutomationProperties.SetName(Calendar,"日期选择");
        var body=new StackPanel();body.Children.Add(Calendar);
        var footer=new DockPanel {LastChildFill=true,Margin=new Thickness(0,10,0,0)};
        var today=new Button {Content="回到今天",Width=96,Height=44,Padding=new Thickness(10,0,10,0),Margin=new Thickness(0,0,12,0)};
        today.SetResourceReference(StyleProperty,"SettingsQuietButton");
        today.Click+=(_,_)=>{Calendar.DisplayMode=CalendarMode.Month;Calendar.SelectedDate=DateTime.Today;Calendar.DisplayDate=DateTime.Today;};
        DockPanel.SetDock(today,Dock.Right);footer.Children.Add(today);
        selection.SetResourceReference(TextBlock.ForegroundProperty,"SettingsMutedBrush");footer.Children.Add(selection);body.Children.Add(footer);
        Children.Add(new ScrollViewer {Content=body,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled});
        Calendar.SelectedDatesChanged+=(_,_)=>ReadSelection();
        Calendar.DisplayDateChanged+=(_,_)=>AnimateMonth();Calendar.DisplayModeChanged+=(_,_)=>AnimateMonth();ReadSelection();
    }
    private void ReadSelection()=>selection.Text=Calendar.SelectedDate is DateTime date?date.ToString("yyyy年M月d日\ndddd",CultureInfo.GetCultureInfo("zh-CN")):"选择一个日期";
    private void AnimateMonth()
    {
        if(!Calendar.IsLoaded||Calendar.Template is null)return;
        if(Calendar.Template.FindName("PART_CalendarItem",Calendar) is not CalendarItem item)return;
        if(item.Template.FindName(Calendar.DisplayMode==CalendarMode.Month?"PART_MonthView":"PART_YearView",item) is not FrameworkElement view)return;
        view.RenderTransform=shift;
        if(GlassMotion.Active&&Math.Abs(view.Opacity-1)<.001&&Math.Abs(shift.X)<.01)
        {view.BeginAnimation(OpacityProperty,null);shift.BeginAnimation(TranslateTransform.XProperty,null);view.Opacity=.7;shift.X=6;}
        GlassMotion.To(view,OpacityProperty,1,140);GlassMotion.To(shift,TranslateTransform.XProperty,0,140);
    }
}
