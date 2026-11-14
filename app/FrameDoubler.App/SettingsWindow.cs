using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using FrameDoubler.Core;

namespace FrameDoubler.App;

// Application settings grouped by category in a left-hand list. Per-job conversion options
// stay in the main window. Every change applies immediately and is saved to settings.json.
public sealed class SettingsWindow:Window
{
    private sealed record Section(string Key,string Glyph,string Label,Func<UIElement> Build);
    private readonly MainWindow owner;
    private bool english;
    private ListBox nav=new();
    private readonly ContentControl page=new();
    private string T(string ko,string en)=>english?en:ko;
    private static SolidColorBrush Brush(string value)=>(SolidColorBrush)new BrushConverter().ConvertFromString(value)!;
    internal ListBox Navigation=>nav;
    internal FrameworkElement? Page=>page.Content as FrameworkElement;
    public SettingsWindow(MainWindow owner,string? category=null,bool preview=false){
        this.owner=owner;if(!preview)Owner=owner;Width=780;Height=560;MinWidth=640;MinHeight=460;
        Style=(Style)FindResource(typeof(Window));WindowStartupLocation=WindowStartupLocation.CenterOwner;WindowStyle=WindowStyle.None;ResizeMode=ResizeMode.CanResize;
        WindowChrome.SetWindowChrome(this,new WindowChrome{CaptionHeight=48,ResizeBorderThickness=new Thickness(6),GlassFrameThickness=new Thickness(0),CornerRadius=new CornerRadius(12),UseAeroCaptionButtons=false});
        SourceInitialized+=(_,_)=>WindowFrame.Attach(this);
        PreviewKeyDown+=(_,e)=>{if(e.Key==Key.Escape){Close();e.Handled=true;}};
        Closed+=(_,_)=>owner.ReturnAdvanced();
        Build(category??"general");
    }
    internal void Select(string key){foreach(ListBoxItem item in nav.Items)if(((Section)item.Tag).Key==key){nav.SelectedItem=item;break;}}
    private void Build(string key){
        english=owner.English;Title=T("설정","Settings");
        var sections=new[]{new Section("general","",T("일반","General"),General),new Section("window","",T("창과 트레이","Window and tray"),WindowAndTray),new Section("advanced","",T("고급","Advanced"),AdvancedPage),
            new Section("data","",T("데이터","Data"),Data),new Section("about","",T("정보","About"),About)};
        nav=new ListBox{Background=Brushes.Transparent,BorderThickness=new Thickness(0),Padding=new Thickness(0),Margin=new Thickness(16,4,12,16)};
        System.Windows.Automation.AutomationProperties.SetAutomationId(nav,"SettingsNav");
        foreach(var section in sections){
            var row=new StackPanel{Orientation=Orientation.Horizontal};
            row.Children.Add(new TextBlock{Text=section.Glyph,FontFamily=new FontFamily("Segoe Fluent Icons"),FontSize=15,Width=28,VerticalAlignment=VerticalAlignment.Center});
            row.Children.Add(new TextBlock{Text=section.Label,FontSize=13,VerticalAlignment=VerticalAlignment.Center,TextWrapping=TextWrapping.NoWrap});
            var item=new ListBoxItem{Style=(Style)FindResource("NavItem"),Tag=section,Content=row};
            System.Windows.Automation.AutomationProperties.SetAutomationId(item,"Nav"+section.Key);System.Windows.Automation.AutomationProperties.SetName(item,section.Label);
            nav.Items.Add(item);
        }
        nav.SelectionChanged+=(_,_)=>{if(nav.SelectedItem is ListBoxItem{Tag:Section section})page.Content=Wrap(section);};

        var body=new Grid();body.ColumnDefinitions.Add(new(){Width=new GridLength(200)});body.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});
        body.Children.Add(nav);
        var divider=new Border{Width=1,Background=Brush("#262D32"),HorizontalAlignment=HorizontalAlignment.Left,Margin=new Thickness(0,4,0,16)};Grid.SetColumn(divider,1);body.Children.Add(divider);
        Grid.SetColumn(page,1);body.Children.Add(page);

        var frame=new Grid();frame.RowDefinitions.Add(new(){Height=new GridLength(48)});frame.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});frame.RowDefinitions.Add(new(){Height=GridLength.Auto});
        var caption=new Grid();
        var name=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(20,0,60,0),VerticalAlignment=VerticalAlignment.Center};
        name.Children.Add(new Image{Source=new BitmapImage(new Uri("pack://application:,,,/Assets/icon.png")),Width=18,Height=18,Margin=new Thickness(0,0,10,0)});
        name.Children.Add(new TextBlock{Text=T("FrameDoubler 설정","FrameDoubler settings"),VerticalAlignment=VerticalAlignment.Center,FontWeight=FontWeights.SemiBold});caption.Children.Add(name);
        var close=new Button{Content="",FontFamily=new FontFamily("Segoe Fluent Icons"),Style=(Style)FindResource("CloseCaptionButton"),HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,0,6,0),ToolTip=T("닫기","Close")};
        WindowChrome.SetIsHitTestVisibleInChrome(close,true);close.Click+=(_,_)=>Close();caption.Children.Add(close);frame.Children.Add(caption);
        Grid.SetRow(body,1);frame.Children.Add(body);
        var footer=new Grid{Margin=new Thickness(24,0,24,20)};
        footer.Children.Add(new TextBlock{Text=Hangul.KeepWords(T("변경 사항은 바로 적용되고 저장됩니다.","Changes apply and save immediately.")),Foreground=(Brush)FindResource("MutedBrush"),FontSize=12,VerticalAlignment=VerticalAlignment.Center});
        var done=new Button{Content=T("닫기","Close"),Style=(Style)FindResource("SecondaryButton"),MinWidth=96,HorizontalAlignment=HorizontalAlignment.Right};done.Click+=(_,_)=>Close();
        System.Windows.Automation.AutomationProperties.SetAutomationId(done,"SettingsClose");footer.Children.Add(done);
        Grid.SetRow(footer,2);frame.Children.Add(footer);
        Content=new Border{Background=Background,BorderBrush=Brush("#30373C"),BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(12),Child=frame};
        Select(key);if(nav.SelectedItem is null)nav.SelectedIndex=0;
    }
    private UIElement Wrap(Section section){
        var stack=new StackPanel{Margin=new Thickness(24,4,24,16)};
        stack.Children.Add(new TextBlock{Text=section.Label,FontSize=20,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,0,0,16)});
        stack.Children.Add(section.Build());
        return new ScrollViewer{Content=stack,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};
    }
    // A surface card holding setting rows separated by hairlines.
    private Border Card(params UIElement[] rows){
        var stack=new StackPanel();
        for(int i=0;i<rows.Length;i++){
            if(i>0)stack.Children.Add(new Border{Height=1,Background=Brush("#2B3338"),Margin=new Thickness(0,14,0,14)});
            stack.Children.Add(rows[i]);
        }
        return new Border{Background=(Brush)FindResource("SurfaceBrush"),CornerRadius=new CornerRadius(10),Padding=new Thickness(18,16,18,16),Margin=new Thickness(0,0,0,14),Child=stack};
    }
    private Grid Row(string title,string? help,UIElement control,string? id=null){
        var grid=new Grid();grid.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});grid.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
        var text=new StackPanel{VerticalAlignment=VerticalAlignment.Center};
        text.Children.Add(new TextBlock{Text=Hangul.KeepWords(title),FontWeight=FontWeights.SemiBold});
        if(!string.IsNullOrEmpty(help))text.Children.Add(new TextBlock{Text=Hangul.KeepWords(help),Foreground=(Brush)FindResource("MutedBrush"),FontSize=12,Margin=new Thickness(0,4,0,0),LineHeight=18});
        grid.Children.Add(text);
        if(control is FrameworkElement element){element.VerticalAlignment=VerticalAlignment.Center;element.Margin=new Thickness(18,0,0,0);if(id is not null)System.Windows.Automation.AutomationProperties.SetAutomationId(element,id);}
        Grid.SetColumn(control,1);grid.Children.Add(control);
        return grid;
    }
    private ComboBox Choices(string selected,Action<string> changed,params Choice[] items){
        var box=new ComboBox{ItemsSource=items,Width=210,Margin=new Thickness(0)};
        box.SelectedItem=items.FirstOrDefault(x=>x.Value==selected)??items[0];
        box.SelectionChanged+=(_,_)=>{if(box.SelectedItem is Choice choice)changed(choice.Value);};
        return box;
    }
    private Button ActionButton(string text,Action click,string style="SecondaryButton"){
        var button=new Button{Content=text,Style=(Style)FindResource(style),MinWidth=110,MinHeight=36,Padding=new Thickness(14,6,14,6)};button.Click+=(_,_)=>click();return button;
    }
    private TextBlock Note(string text)=>new(){Text=Hangul.KeepWords(text),Foreground=(Brush)FindResource("MutedBrush"),FontSize=12,LineHeight=18,Margin=new Thickness(2,0,0,0)};

    private UIElement General(){
        var updates=new CheckBox{IsChecked=owner.Settings.CheckUpdates,Content=""};
        updates.Checked+=(_,_)=>owner.ChangeSettings(p=>p with{CheckUpdates=true});updates.Unchecked+=(_,_)=>owner.ChangeSettings(p=>p with{CheckUpdates=false});
        var stack=new StackPanel();
        // Language is not repeated here: the 한국어 | EN switch in the title bar is always one click away.
        stack.Children.Add(Card(Row(T("시작할 때 업데이트 확인","Check for updates at startup"),T("GitHub의 안정 릴리스를 확인하고, 새 버전이 있으면 업데이트 버튼을 주황색으로 표시합니다.","Looks up the stable GitHub release and marks the update button orange when a new version exists."),updates,"SettingCheckUpdates")));
        return stack;
    }
    private UIElement WindowAndTray(){
        var close=Choices(owner.Settings.CloseAction,value=>owner.ChangeSettings(p=>p with{CloseAction=value}),
            new Choice("ask",T("매번 묻기","Ask every time")),new Choice("tray",T("트레이로 최소화","Minimize to tray")),new Choice("exit",T("종료","Exit")));
        var minimize=Choices(owner.Settings.MinimizeAction,value=>owner.ChangeSettings(p=>p with{MinimizeAction=value}),
            new Choice("taskbar",T("작업 표시줄 (기본값)","Taskbar (default)")),new Choice("tray",T("트레이로 최소화","Minimize to tray")));
        var stack=new StackPanel();
        stack.Children.Add(Card(Row(T("닫기(X) 버튼을 누르면","Close (X) button"),T("Alt+F4에도 적용됩니다.","Also applies to Alt+F4."),close,"SettingCloseAction"),
            Row(T("최소화하면","When minimized"),T("기본은 작업 표시줄입니다. 원하면 트레이로 바꿀 수 있습니다.","The default is the taskbar. Choose the tray if you prefer."),minimize,"SettingMinimizeAction")));
        stack.Children.Add(Note(T("트레이로 최소화하면 창이 사라져도 작업이 계속되며, 트레이 아이콘을 두 번 클릭하면 창이 다시 열립니다. 진행 중인 작업이 있을 때 종료하면 설정과 관계없이 확인합니다.",
            "In the tray, work continues without a window; double-click the tray icon to open it again. Exiting while work is running always asks first.")));
        return stack;
    }
    // The main window's own advanced controls, lent to this page (see MainWindow.BorrowAdvanced).
    private UIElement AdvancedPage(){
        var stack=new StackPanel();
        stack.Children.Add(new Border{Background=(Brush)FindResource("SurfaceBrush"),CornerRadius=new CornerRadius(10),Padding=new Thickness(18,16,18,14),Margin=new Thickness(0,0,0,14),Child=owner.BorrowAdvanced()});
        stack.Children.Add(Note(T("추론 장치, 인코더, 코덱은 이 PC에서 실제로 동작을 확인한 항목만 보여 줍니다. 바꾼 값은 다음 변환과 예상 소요 시간에 바로 반영됩니다.",
            "Only devices, encoders and codecs that actually worked on this PC are listed. Changes apply to the next conversion and the time estimate.")));
        return stack;
    }
    private UIElement Data(){
        string settings=Path.GetFullPath(Preferences.DirectoryPath),work=Path.GetFullPath(EncoderDetector.WorkRoot);
        var estimates=new TextBlock();
        void Count(){int n=TimeEstimates.Count();estimates.Text=T($"저장된 측정 {n}개",$"{n} saved measurements");}
        var reset=ActionButton(T("초기화","Reset"),()=>{
            if(!Confirm(T("예상 시간 기록을 초기화할까요?","Reset time estimate history?"),T("저장된 측정이 지워지고, 다음 예상은 실제 측정으로 다시 시작합니다.","Saved measurements are removed and the next estimate measures again.")))return;
            TimeEstimates.Reset();Count();});
        var workSize=new TextBlock();
        void Size(){workSize.Text=$"{work} · {Bytes(FolderSize(work))}";}
        var clean=ActionButton(T("정리","Clean up"),()=>{
            if(owner.Working)return;
            if(!Confirm(T("작업 파일을 정리할까요?","Clean up working files?"),T("장치·인코더 확인 결과와 임시 파일을 지웁니다. 다음 실행 때 장치를 다시 확인합니다.","Removes device/encoder check results and temporary files. Devices are checked again at the next start.")))return;
            ClearFolder(work);Size();});
        clean.IsEnabled=!owner.Working;
        Count();Size();
        var stack=new StackPanel();
        stack.Children.Add(Card(
            Row(T("설정과 기록 위치","Settings and history"),settings,ActionButton(T("폴더 열기","Open folder"),()=>Open(settings)),"SettingOpenData"),
            Row(T("예상 시간 기록","Time estimate history"),null,reset,"SettingResetEstimates"),
            Row(T("작업 파일","Working files"),null,clean,"SettingCleanWork")));
        // Live values below the rows they belong to.
        var card=(StackPanel)((Border)stack.Children[0]).Child;
        ((StackPanel)((Grid)card.Children[2]).Children[0]).Children.Add(Styled(estimates));
        ((StackPanel)((Grid)card.Children[4]).Children[0]).Children.Add(Styled(workSize));
        stack.Children.Add(Note(T("설정(settings.json)과 예상 시간 기록(performance.json)은 실행 파일이 있는 폴더에 저장됩니다.","Settings (settings.json) and time estimates (performance.json) are stored next to the executable.")));
        return stack;
        TextBlock Styled(TextBlock text){text.Foreground=(Brush)FindResource("MutedBrush");text.FontSize=12;text.Margin=new Thickness(0,4,0,0);text.TextWrapping=TextWrapping.Wrap;return text;}
    }
    private UIElement About(){
        var head=new Grid{Margin=new Thickness(0,0,0,14)};head.ColumnDefinitions.Add(new(){Width=GridLength.Auto});head.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});
        head.Children.Add(new Image{Source=new BitmapImage(new Uri("pack://application:,,,/Assets/icon.png")),Width=48,Height=48,Margin=new Thickness(0,0,16,0)});
        var title=new StackPanel{VerticalAlignment=VerticalAlignment.Center};
        title.Children.Add(new TextBlock{Text="FrameDoubler",FontSize=18,FontWeight=FontWeights.SemiBold});
        title.Children.Add(new TextBlock{Text=T($"버전 {Updates.Current}",$"Version {Updates.Current}"),Foreground=(Brush)FindResource("MutedBrush"),Margin=new Thickness(0,3,0,0)});
        Grid.SetColumn(title,1);head.Children.Add(title);
        // No update button here: the title bar already has one.
        var links=new WrapPanel();
        string notices=Path.Combine(AppContext.BaseDirectory,"THIRD_PARTY_NOTICES.md");
        foreach(var (label,target) in new[]{(T("GitHub 저장소","GitHub repository"),"https://github.com/deuxdoom/FrameDoubler"),
            (T("변경 기록","Changelog"),"https://github.com/deuxdoom/FrameDoubler/blob/main/CHANGELOG.md"),
            (T("라이선스 고지","License notices"),File.Exists(notices)?notices:"https://github.com/deuxdoom/FrameDoubler/blob/main/THIRD_PARTY_NOTICES.md")}){
            var link=new Button{Content=label,Style=(Style)FindResource("TextButton"),Foreground=Brush("#FF8C60"),Padding=new Thickness(0,4,0,4),Margin=new Thickness(0,0,20,0)};
            link.Click+=(_,_)=>Open(target);links.Children.Add(link);
        }
        var stack=new StackPanel();
        stack.Children.Add(head);
        stack.Children.Add(Card(links));
        stack.Children.Add(Note(T("FrameDoubler는 MIT 라이선스로 배포하는 독립 프로젝트입니다.","FrameDoubler is an independent project released under the MIT License.")));
        return stack;
    }
    private bool Confirm(string title,string message){
        var dialog=new BrandDialog(this,T("설정","Settings"));
        dialog.Configure(BrandDialog.Glyphs.Warning,title,message,null,new DialogButton(T("취소","Cancel"),DialogChoice.Cancel,"TextButton"),new DialogButton(T("확인","OK"),DialogChoice.Primary,"PrimaryButton",true));
        dialog.ShowDialog();return dialog.Choice==DialogChoice.Primary;
    }
    private static void Open(string target){try{Process.Start(new ProcessStartInfo(target){UseShellExecute=true});}catch(Exception ex)when(ex is System.ComponentModel.Win32Exception or InvalidOperationException){}}
    private static long FolderSize(string folder){
        try{return Directory.Exists(folder)?new DirectoryInfo(folder).EnumerateFiles("*",SearchOption.AllDirectories).Sum(x=>x.Length):0;}
        catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){return 0;}
    }
    private static string Bytes(long value)=>value>=1<<30?$"{value/1073741824d:0.0} GB":value>=1<<20?$"{value/1048576d:0.0} MB":$"{value/1024d:0} KB";
    // Never empties a drive root or the program folder itself, even if FRAMEDOUBLER_WORK_ROOT points there.
    private static void ClearFolder(string folder){
        string full=Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        if(!Directory.Exists(full)||Path.GetPathRoot(full)==full+Path.DirectorySeparatorChar||string.Equals(full,Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory),StringComparison.OrdinalIgnoreCase))return;
        foreach(string file in Directory.EnumerateFiles(full))try{File.Delete(file);}catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){}
        foreach(string dir in Directory.EnumerateDirectories(full))try{Directory.Delete(dir,true);}catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){}
    }
}
