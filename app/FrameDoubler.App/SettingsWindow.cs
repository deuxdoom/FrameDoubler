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
// stay in the main window. Every change applies immediately and is saved to settings.ini.
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
        this.owner=owner;if(!preview)Owner=owner;Width=740;Height=520;
        Style=(Style)FindResource(typeof(Window));WindowStartupLocation=WindowStartupLocation.CenterOwner;WindowStyle=WindowStyle.None;ResizeMode=ResizeMode.NoResize;
        WindowChrome.SetWindowChrome(this,new WindowChrome{CaptionHeight=48,ResizeBorderThickness=new Thickness(0),GlassFrameThickness=new Thickness(0),CornerRadius=new CornerRadius(12),UseAeroCaptionButtons=false});
        SourceInitialized+=(_,_)=>WindowFrame.Attach(this);
        PreviewKeyDown+=(_,e)=>{if(e.Key==Key.Escape){Close();e.Handled=true;}};
        Closed+=(_,_)=>owner.ReturnAdvanced();
        Build(category??"general");
    }
    internal void Select(string key){foreach(ListBoxItem item in nav.Items)if(((Section)item.Tag).Key==key){nav.SelectedItem=item;break;}}
    private void Build(string key){
        english=owner.English;Title=T("설정","Settings");
        var sections=new[]{new Section("general","",T("일반","General"),General),new Section("capture","\uE714",T("캡처","Capture"),CapturePage),new Section("shortcuts","\uE765",T("단축키","Shortcuts"),ShortcutsPage),new Section("window","",T("창과 트레이","Window and tray"),WindowAndTray),new Section("advanced","",T("고급","Advanced"),AdvancedPage),
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

    // Capture preview: where screenshots and recordings go, the recording format, start page.
    private UIElement CapturePage(){
        var stack=new StackPanel();
        UIElement Folder(Func<Preferences,string> current,Func<Preferences,string> effective,Func<Preferences,string,Preferences> set,string title){
            var path=new TextBlock{Foreground=(Brush)FindResource("MutedBrush"),FontSize=12,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,4,0,8)};
            var buttons=new StackPanel{Orientation=Orientation.Horizontal};
            void Refresh()=>path.Text=effective(owner.Settings)+(current(owner.Settings).Length==0?T("  (기본값)","  (default)"):"");
            buttons.Children.Add(ActionButton(T("변경…","Change…"),()=>{
                using var dialog=new System.Windows.Forms.FolderBrowserDialog{SelectedPath=effective(owner.Settings),UseDescriptionForTitle=true,Description=title,ShowNewFolderButton=true};
                if(dialog.ShowDialog()==System.Windows.Forms.DialogResult.OK){owner.ChangeSettings(p=>set(p,dialog.SelectedPath));Refresh();}}));
            var open=ActionButton(T("열기","Open"),()=>{try{Directory.CreateDirectory(effective(owner.Settings));Process.Start(new ProcessStartInfo("explorer.exe",'"'+effective(owner.Settings)+'"'){UseShellExecute=true});}catch(Exception){}});
            open.Margin=new Thickness(8,0,0,0);buttons.Children.Add(open);
            var reset=ActionButton(T("기본값","Default"),()=>{owner.ChangeSettings(p=>set(p,""));Refresh();},"TextButton");reset.Margin=new Thickness(12,0,0,0);buttons.Children.Add(reset);
            Refresh();
            var block=new StackPanel();block.Children.Add(new TextBlock{Text=Hangul.KeepWords(title),FontWeight=FontWeights.SemiBold});block.Children.Add(path);block.Children.Add(buttons);
            return block;
        }
        stack.Children.Add(Card(
            Folder(p=>p.ScreenshotDirectory,p=>p.EffectiveScreenshotDirectory,(p,v)=>p with{ScreenshotDirectory=v},T("스크린샷 저장 위치","Screenshot folder")),
            Folder(p=>p.RecordingDirectory,p=>p.EffectiveRecordingDirectory,(p,v)=>p with{RecordingDirectory=v},T("녹화 저장 위치","Recording folder"))));
        var codec=Choices(owner.Settings.RecordingCodec,v=>owner.ChangeSettings(p=>p with{RecordingCodec=v}),
            new Choice("h264",T("H.264 (호환성 우선)","H.264 (most compatible)")),new Choice("hevc",T("HEVC (작은 파일)","HEVC (smaller files)")));
        var rates=new[]{new Choice("0",T("자동 (해상도·fps 기준)","Automatic (size and rate)"))}
            .Concat(new[]{20,40,60,80,120}.Select(m=>new Choice(m.ToString(System.Globalization.CultureInfo.InvariantCulture),m+" Mbps"))).ToArray();
        var bitrate=Choices(owner.Settings.RecordingBitrate.ToString(System.Globalization.CultureInfo.InvariantCulture),
            v=>owner.ChangeSettings(p=>p with{RecordingBitrate=int.Parse(v,System.Globalization.CultureInfo.InvariantCulture)}),rates);
        var start=Choices(owner.Settings.StartPage,v=>owner.ChangeSettings(p=>p with{StartPage=v}),
            new Choice("capture",T("캡처 미리보기","Capture preview")),new Choice("convert",T("파일 변환","Convert files")));
        stack.Children.Add(Card(
            Row(T("녹화 코덱","Recording codec"),T("화면과 소리를 MP4에 기록합니다. H.264는 조각 MP4, HEVC는 정지 후 파일 저장이 완료됩니다.","Records picture and sound to MP4. H.264 uses fragmented MP4; HEVC finishes saving when stopped."),codec,"SettingRecordCodec"),
            Row(T("녹화 비트레이트","Recording bitrate"),T("자동은 4K60 H.264에서 약 55Mbps입니다.","Automatic is about 55 Mbps for 4K60 H.264."),bitrate,"SettingRecordBitrate"),
            Row(T("시작 화면","Start page"),T("마지막으로 쓴 화면을 기억합니다.","The last used page is remembered."),start,"SettingStartPage")));
        stack.Children.Add(Note(T("새 위치와 형식은 다음 스크린샷·녹화부터 적용됩니다.","New folders and formats apply from the next screenshot or recording.")));
        if(LiveEngine.Available)stack.Children.Add(VfxCard());
        return stack;
    }
    // NVIDIA VFX frame generation is an optional download: nothing is fetched before the user accepts NVIDIA's license.
    private UIElement VfxCard(){
        string engine=LiveEngine.Directory;
        bool bundled=VfxRuntime.Complete(Path.Combine(engine,"nvvfx"));
        string Status()=>bundled?T("이 빌드에 포함되어 있습니다.","Included in this build.")
            :VfxRuntime.Downloaded(engine)?T("설치됨 · 프레임 생성 방식에서 NVIDIA VFX를 고를 수 있습니다.","Installed · NVIDIA VFX can be selected as the frame generation method.")
            :T("미설치 · 설치하지 않아도 프레임 생성은 동작합니다.","Not installed · frame generation works without it.");
        var state=Note(Status());
        var bar=new ProgressBar{Height=4,Minimum=0,Maximum=1,Margin=new Thickness(0,8,0,0),Visibility=Visibility.Collapsed};
        var install=ActionButton(T("받기…","Download…"),()=>{});var remove=ActionButton(T("제거","Remove"),()=>{});remove.Margin=new Thickness(8,0,0,0);
        var cancel=ActionButton(T("취소","Cancel"),()=>{});cancel.Margin=new Thickness(8,0,0,0);
        var actions=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(0,8,0,0)};actions.Children.Add(install);actions.Children.Add(remove);actions.Children.Add(cancel);
        System.Threading.CancellationTokenSource? download=null;bool working=false;
        void Buttons(){bool have=VfxRuntime.Downloaded(engine);install.IsEnabled=!working&&!bundled&&!have;remove.IsEnabled=!working&&have;
            cancel.Visibility=working?Visibility.Visible:Visibility.Collapsed;bar.Visibility=working?Visibility.Visible:Visibility.Collapsed;}
        cancel.Click+=(_,_)=>download?.Cancel();
        Closing+=(_,e)=>{if(working){e.Cancel=true;download?.Cancel();}};
        install.Click+=async(_,_)=>{
            try{
                string terms=T($"NVIDIA 공식 패키지 저장소(pypi.nvidia.com)에서 NVIDIA VFX 런타임 {VfxRuntime.DownloadBytes/1_000_000d:0}MB를 받고, 프레임 생성에 필요한 파일 {VfxRuntime.InstalledBytes/1_000_000d:0}MB만 남깁니다.\n\n이 런타임은 NVIDIA의 독점 소프트웨어이며 NVIDIA Software License Agreement와 AI 제품별 조건이 적용됩니다. 라이선스 문서는 설치 폴더의 licenses에 함께 저장합니다. NVIDIA RTX GPU와 최신 드라이버가 필요합니다.\n\n파일의 SHA-256을 확인한 뒤 설치합니다. NVIDIA 라이선스에 동의하고 받을까요?",
                    $"Download the NVIDIA VFX runtime ({VfxRuntime.DownloadBytes/1_000_000d:0} MB) from NVIDIA's official package index (pypi.nvidia.com); only the {VfxRuntime.InstalledBytes/1_000_000d:0} MB needed for frame generation is kept.\n\nThe runtime is NVIDIA proprietary software under the NVIDIA Software License Agreement and the product-specific terms for AI products. The license documents are saved in the licenses folder next to it. An NVIDIA RTX GPU and a current driver are required.\n\nSHA-256 is checked before installing. Accept NVIDIA's license and download?");
                if(!Confirm(T("NVIDIA VFX 런타임 받기","Download NVIDIA VFX runtime"),terms))return;
                working=true;download=new();Buttons();
                var progress=new Progress<(double Fraction,string Stage)>(x=>{bar.Value=x.Fraction;
                    state.Text=Hangul.KeepWords(x.Stage=="download"?T($"받는 중… {x.Fraction/.9*100:0}%",$"Downloading… {x.Fraction/.9*100:0}%"):T("파일 확인 중…","Verifying files…"));});
                string target=engine;await System.Threading.Tasks.Task.Run(()=>VfxRuntime.InstallAsync(target,progress,download.Token));
                state.Text=Hangul.KeepWords(T("설치 완료 · 미리보기를 다시 시작하면 적용됩니다.","Installed · applies when the preview is restarted."));
            }catch(OperationCanceledException){state.Text=T("받기를 취소했습니다.","Download cancelled.");}
            catch(Exception ex){state.Text=Hangul.KeepWords(ex.Message);}
            finally{working=false;download?.Dispose();download=null;Buttons();owner.Capture?.SettingsChanged();}
        };
        remove.Click+=(_,_)=>{
            try{VfxRuntime.Remove(engine);state.Text=Hangul.KeepWords(Status());}
            catch(Exception){state.Text=Hangul.KeepWords(T("사용 중이라 제거하지 못했습니다. 프로그램을 다시 시작한 뒤 미리보기를 켜기 전에 제거하세요.","In use; restart the program and remove it before starting the preview."));}
            Buttons();owner.Capture?.SettingsChanged();};
        var body=new StackPanel();
        body.Children.Add(new TextBlock{Text=Hangul.KeepWords(T("NVIDIA VFX 프레임 생성 (선택)","NVIDIA VFX frame generation (optional)")),FontWeight=FontWeights.SemiBold});
        body.Children.Add(Note(T("NVIDIA의 영상용 AI 프레임 생성 모델입니다. 용량이 커서 패키지에 넣지 않고, 원할 때만 받습니다.","NVIDIA's AI frame generation model for video. It is large, so it is downloaded only on request.")));
        body.Children.Add(state);body.Children.Add(bar);body.Children.Add(actions);Buttons();return Card(body);
    }
    // Capture shortcuts: click a box, then press the keys. Esc cancels, Delete clears.
    private sealed record ShortcutAction(string Key,string Ko,string En,string HelpKo,string HelpEn,Func<Preferences,string> Get,Func<Preferences,string,Preferences> Set);
    private UIElement ShortcutsPage(){
        var stack=new StackPanel();
        var actions=new[]{
            new ShortcutAction("screenshot","스크린샷","Screenshot","처리된 화면을 PNG로 저장합니다.","Saves the processed picture as PNG.",p=>p.ShortcutScreenshot,(p,v)=>p with{ShortcutScreenshot=v}),
            new ShortcutAction("record","녹화 시작 / 중지","Start / stop recording","처리된 화면과 소리를 MP4로 녹화합니다.","Records the processed picture and sound to MP4.",p=>p.ShortcutRecord,(p,v)=>p with{ShortcutRecord=v}),
            new ShortcutAction("fullscreen","전체 화면","Full screen","Esc로도 빠져나옵니다.","Esc also leaves it.",p=>p.ShortcutFullscreen,(p,v)=>p with{ShortcutFullscreen=v}),
            new ShortcutAction("fg","프레임 생성 켜기 / 끄기","Frame generation on / off","바로 비교해 볼 때 씁니다.","For quick comparisons.",p=>p.ShortcutFrameGeneration,(p,v)=>p with{ShortcutFrameGeneration=v}),
            new ShortcutAction("stats","통계 패널","Statistics panels","신호·GPU·프레임 속도 패널을 접거나 폅니다.","Shows or hides the statistics panels.",p=>p.ShortcutStats,(p,v)=>p with{ShortcutStats=v}),
            new ShortcutAction("original","원본 보기 (누르는 동안)","Show original (while held)","누르고 있는 동안 처리 전 캡처 화면을 보여 줍니다.","Shows the unprocessed capture while held.",p=>p.ShortcutOriginal,(p,v)=>p with{ShortcutOriginal=v})};
        var boxes=new System.Collections.Generic.Dictionary<string,Button>();
        void RefreshAll(){foreach(var a in actions)boxes[a.Key].Content=string.IsNullOrEmpty(a.Get(owner.Settings))?T("없음","None"):a.Get(owner.Settings);}
        var rows=new System.Collections.Generic.List<UIElement>();
        foreach(var a in actions){
            var box=new Button{Style=(Style)FindResource("SecondaryButton"),MinWidth=150,MinHeight=34,Padding=new Thickness(12,4,12,4),FontFamily=new FontFamily("Cascadia Mono, Consolas")};
            boxes[a.Key]=box;bool capturing=false;
            box.Click+=(_,_)=>{capturing=true;box.Content=T("키를 누르세요…","Press keys…");box.Focus();};
            box.LostKeyboardFocus+=(_,_)=>{if(capturing){capturing=false;RefreshAll();}};
            box.PreviewKeyDown+=(_,e)=>{
                if(!capturing)return;
                var key=e.Key==Key.System?e.SystemKey:e.Key;e.Handled=true;
                if(Shortcut.IsModifier(key))return;
                capturing=false;
                if(key==Key.Escape){RefreshAll();return;}
                string value=key is Key.Delete or Key.Back?"":Shortcut.Format(Keyboard.Modifiers,key);
                // One action per combination: a key taken from another action clears it there.
                owner.ChangeSettings(p=>{foreach(var other in actions)if(other.Key!=a.Key&&value.Length>0&&other.Get(p)==value)p=other.Set(p,"");return a.Set(p,value);});
                RefreshAll();
            };
            rows.Add(Row(T(a.Ko,a.En),T(a.HelpKo,a.HelpEn),box,"Shortcut"+a.Key));
        }
        RefreshAll();
        stack.Children.Add(Card(rows.ToArray()));
        var global=new CheckBox{IsChecked=owner.Settings.GlobalShortcuts,Content=""};
        global.Checked+=(_,_)=>owner.ChangeSettings(p=>p with{GlobalShortcuts=true});global.Unchecked+=(_,_)=>owner.ChangeSettings(p=>p with{GlobalShortcuts=false});
        var defaults=ActionButton(T("기본값으로","Restore defaults"),()=>{var d=new Preferences();owner.ChangeSettings(p=>p with{ShortcutScreenshot=d.ShortcutScreenshot,ShortcutRecord=d.ShortcutRecord,
            ShortcutFullscreen=d.ShortcutFullscreen,ShortcutFrameGeneration=d.ShortcutFrameGeneration,ShortcutStats=d.ShortcutStats,ShortcutOriginal=d.ShortcutOriginal});RefreshAll();});
        stack.Children.Add(Card(Row(T("다른 창에서도 동작","Work in other windows"),T("게임이나 다른 프로그램이 앞에 있어도 스크린샷·녹화·전체 화면·프레임 생성·통계 단축키가 동작합니다. 원본 보기는 누르는 동안 동작해야 해서 FrameDoubler 창에서만 씁니다.",
            "Screenshot, record, full screen, frame generation and statistics shortcuts also work while a game or another program is in front. Show original needs the key to be held, so it stays in the FrameDoubler window."),global,"SettingGlobalShortcuts"),
            Row(T("기본 단축키","Default keys"),"F7 · F8 · F11 · F6 · F10 · V",defaults)));
        stack.Children.Add(Note(T("상자를 누른 뒤 원하는 키 조합을 누르세요. Esc는 취소, Delete는 지우기입니다.","Click a box, then press the key combination. Esc cancels, Delete clears.")));
        return stack;
    }
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
        if(File.Exists(Acceleration.Engine(AppPaths.Discover())))stack.Children.Add(AccelerationCard());
        return stack;
    }
    private UIElement AccelerationCard(){
        var paths=AppPaths.Discover();
        var selected=Choices(owner.Settings.Accelerator,value=>owner.ChangeSettings(p=>p with{Accelerator=value}),
            new Choice("auto",T("자동 (설치한 기능 사용)","Auto (use installed add-on)")),new Choice("vulkan",T("기본 Vulkan","Default Vulkan")),new Choice("rtx","TensorRT RTX"));
        var state=Note(Acceleration.Installed(paths)?T("추가 기능 설치됨 · RGB8 영상에서 사용합니다.","Add-on installed · used for RGB8 video."):T("추가 기능 미설치 · 기본 Vulkan으로 처리합니다.","Add-on absent · using default Vulkan."));
        var install=ActionButton(T("설치…","Install…"),()=>{});var remove=ActionButton(T("제거","Remove"),()=>{});
        var cancel=ActionButton(T("취소","Cancel"),()=>{});cancel.Visibility=Visibility.Collapsed;
        var actions=new StackPanel{Orientation=Orientation.Horizontal};actions.Children.Add(install);actions.Children.Add(remove);actions.Children.Add(cancel);
        System.Threading.CancellationTokenSource? download=null;bool working=false;
        void Buttons(){install.IsEnabled=!working&&owner.CanManageAcceleration&&!Acceleration.Installed(paths);remove.IsEnabled=!working&&owner.CanManageAcceleration&&Acceleration.Installed(paths);selected.IsEnabled=!working;cancel.Visibility=working?Visibility.Visible:Visibility.Collapsed;}
        cancel.Click+=(_,_)=>download?.Cancel();
        Closing+=(_,e)=>{if(working){e.Cancel=true;download?.Cancel();}};
        install.Click+=async(_,_)=>{
            try{
                if(!owner.CanManageAcceleration){state.Text=T("현재 작업이 끝난 뒤 설치할 수 있습니다.","Wait for the current operation before installing.");return;}
                await Acceleration.CheckHostAsync();
                string terms=T($"공식 PyPI에서 {Acceleration.DownloadBytes/1_000_000d:0.0}MB를 다운로드합니다. 설치 후 약 310MB를 추가 사용합니다.\n\nONNX Runtime: MIT\nRTX EP: Apache-2.0\nTensorRT RTX 런타임: NVIDIA Software License Agreement\nhttps://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/reference/sla.html\n\n받는 곳: pypi.org / files.pythonhosted.org\n파일별 SHA-256을 확인하고 실제 GPU 추론을 검사합니다. 위 라이선스에 동의하고 설치할까요?",
                    $"Download {Acceleration.DownloadBytes/1_000_000d:0.0} MB from official PyPI; about 310 MB installed.\n\nONNX Runtime: MIT\nRTX EP: Apache-2.0\nTensorRT RTX runtime: NVIDIA Software License Agreement\nhttps://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/reference/sla.html\n\nSources: pypi.org / files.pythonhosted.org\nSHA-256 and real GPU inference are checked. Accept these licenses and install?");
                if(!Confirm(T("GPU 가속 추가 기능 설치","Install GPU acceleration add-on"),terms))return;
                working=true;download=new();Buttons();
                await Acceleration.InstallAsync(paths,new Progress<string>(text=>state.Text=Hangul.KeepWords(text)),download.Token);
                state.Text=T("설치 완료 · 다음 변환부터 적용합니다.","Installed · applies to the next conversion.");
                owner.RefreshAcceleration();
            }catch(OperationCanceledException){state.Text=T("설치를 취소했습니다.","Installation cancelled.");}
            catch(Exception ex){state.Text=Hangul.KeepWords(ex.Message);}
            finally{working=false;download?.Dispose();download=null;Buttons();}
        };
        remove.Click+=(_,_)=>{try{if(!owner.CanManageAcceleration){state.Text=T("현재 작업이 끝난 뒤 제거할 수 있습니다.","Wait for the current operation before removing.");return;}Acceleration.Remove(paths);state.Text=T("제거 완료 · 기본 Vulkan을 사용합니다.","Removed · using default Vulkan.");owner.RefreshAcceleration();}catch(Exception ex){state.Text=Hangul.KeepWords(ex.Message);}Buttons();};
        var body=new StackPanel();body.Children.Add(Row(T("GPU 가속 추가 기능","GPU acceleration add-on"),T("선택 기능 · 원본 해상도와 FP32 정밀도 유지","Optional · preserves source resolution and FP32 precision"),selected,"SettingAccelerator"));
        body.Children.Add(state);body.Children.Add(actions);Buttons();return Card(body);
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
        stack.Children.Add(Note(T("설정(settings.ini)과 예상 시간 기록(performance.json)은 실행 파일이 있는 폴더에 저장됩니다.","Settings (settings.ini) and time estimates (performance.json) are stored next to the executable.")));
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
