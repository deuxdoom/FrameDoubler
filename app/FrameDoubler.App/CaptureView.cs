using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using FrameDoubler.Core;

namespace FrameDoubler.App;

// Capture card preview: source and mode, live picture, frame generation / quality / audio /
// display options and live statistics. The picture itself is drawn by the native engine
// (poc/live) straight into a child window; this view only steers it.
public sealed class CaptureView:UserControl
{
    private readonly MainWindow owner;
    private readonly List<Action<bool>> texts=new();
    private bool English=>owner.English;
    private string T(string ko,string en)=>English?en:ko;
    private static SolidColorBrush Brush(string value){var b=(SolidColorBrush)new BrushConverter().ConvertFromString(value)!;b.Freeze();return b;}
    private static readonly Brush Muted=Brush("#A6B1B8"),Faint=Brush("#6F7C84"),Text=Brush("#F3F1EB"),Surface=Brush("#1A2024"),Line=Brush("#2B3338"),
        Accent=Brush("#FF8C60"),Good=Brush("#5BD38A"),Warn=Brush("#F4BD83"),Bad=Brush("#FF6B6B");
    private static readonly FontFamily Mono=new("Cascadia Mono, Consolas"),Icons=new("Segoe Fluent Icons");

    private CaptureInventory? inventory;
    private LiveSession? session;
    private LiveOptions options;
    // 2 until the driver reports DLSS multi-frame generation; the saved multiplier is kept and
    // applied again once the capability allows it.
    private int maxMultiplier=2;
    private LiveStatus? last;
    private readonly GpuUsage gpuUsage=new();
    private readonly DispatcherTimer timer=new(){Interval=TimeSpan.FromMilliseconds(250)};
    private bool updating,starting,immersive,loaded;
    private string statusMessage="";
    private string captureFailure="";
    private readonly ComboBox audioOutputBox=new(){Margin=new Thickness(0,8,0,0)};

    // Controls kept for updates.
    private readonly ComboBox deviceBox=new(){Width=300,Margin=new Thickness(0)},modeBox=new(){Width=220,Margin=new Thickness(10,0,0,0)},audioBox=new(){Margin=new Thickness(0,8,0,0)};
    private readonly TextBlock signalChip=new(),outputChip=new(),recordText=new(),liveText=new(),placeholderTitle=new(),placeholderHint=new(),pathText=new(),contentHint=new(),vfxState=new();
    private readonly Button startButton=new(),screenshotButton=new(),recordButton=new(),fullscreenButton=new();
    private readonly Ellipse recordDot=new(){Width=10,Height=10,Fill=Bad,Margin=new Thickness(0,0,8,0)};
    private readonly Ellipse statusDot=new(){Width=9,Height=9,Margin=new Thickness(0,4,9,0),VerticalAlignment=VerticalAlignment.Top};
    private readonly PreviewHost host=new();
    private readonly Grid previewArea=new();
    private readonly Grid previewSpace=new();
    private readonly Border previewFrame=new();
    private readonly Border placeholder=new();
    private readonly Grid layout=new();
    private readonly Border sidePanel=new(),statsRow=new(),topBar=new(),liveBar=new();
    private readonly ContentControl tabContent=new();
    private readonly Dictionary<string,TextBlock> values=new();
    private readonly BudgetBar processingGauge=new(),gpuGauge=new(),loadGauge=new();
    private readonly Sparkline fpsHistory=new(){Height=26};
    private readonly TextBlock bigFps=new(){FontSize=30,FontWeight=FontWeights.SemiBold,FontFamily=Mono,Text="—"},contentFps=new(){FontSize=18,FontFamily=Mono,Text="—",VerticalAlignment=VerticalAlignment.Bottom,Margin=new Thickness(0,0,0,4)},featureText=new(){FontSize=12,Foreground=Muted,TextWrapping=TextWrapping.Wrap,TextTrimming=TextTrimming.CharacterEllipsis,Height=42,Margin=new Thickness(0,8,0,0)};
    private readonly Dictionary<string,ToggleButton> toggles=new();
    private readonly Dictionary<string,List<RadioButton>> segments=new();
    private readonly Slider volume=new(){Minimum=0,Maximum=100,Width=170},offset=new(){Minimum=-200,Maximum=500,Width=170,TickFrequency=10,IsSnapToTickEnabled=true};
    private readonly TextBlock volumeValue=new(){FontFamily=Mono,Width=44,TextAlignment=TextAlignment.Right,VerticalAlignment=VerticalAlignment.Center},offsetValue=new(){FontFamily=Mono,Width=60,TextAlignment=TextAlignment.Right,VerticalAlignment=VerticalAlignment.Center};
    private readonly ComboBox methodBox=new(){Width=200,Margin=new Thickness(0)};
    private string tab="fg";
    private readonly Dictionary<string,UIElement> tabPages=new();
    private readonly Dictionary<string,RadioButton> tabButtons=new();

    public event Action<bool>? ImmersiveChanged;
    public bool Recording=>last?.Recording==true&&session is not null;
    public bool Saving=>last?.RecordingFinalizing==true&&session is not null;
    public bool StartingRecording=>last?.RecordingStarting==true&&session is not null;
    public bool Running=>session is not null;

    public CaptureView(MainWindow owner){
        this.owner=owner;
        options=owner.Settings.LiveOptions;
        options=options with{Multiplier=Math.Min(options.Multiplier,maxMultiplier)};
        Build();
        ShowMultiplierChoices();
        timer.Tick+=(_,_)=>Poll();
        host.DoubleClicked+=()=>SetImmersive(!immersive);
        host.KeyPressed+=(key,mods)=>HandleShortcut(key,mods);
        host.KeyReleased+=key=>HandleKeyUp(key);
        SizeChanged+=(_,_)=>Adapt();
    }

    // ---------------------------------------------------------------- layout
    private void L(Action<string> apply,string ko,string en){void Set(bool english)=>apply(english?en:ko);texts.Add(Set);Set(English);}
    private TextBlock Label(string ko,string en,double size=13,Brush? brush=null,FontWeight? weight=null){
        var t=new TextBlock{FontSize=size,Foreground=brush??Text,FontWeight=weight??FontWeights.Normal,TextWrapping=TextWrapping.Wrap};
        L(s=>t.Text=Hangul.KeepWords(s),ko,en);return t;
    }
    public void Translate(){
        foreach(var apply in texts)apply(English);
        FillDevices(keepSelection:true);
        UpdateRecordButton();UpdateStart();ShowStatus(last);
    }
    private Button IconButton(string glyph,string ko,string en,Action click,string style="SecondaryButton"){
        var content=new StackPanel{Orientation=Orientation.Horizontal};
        content.Children.Add(new TextBlock{Text=glyph,FontFamily=Icons,FontSize=14,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,8,0)});
        var label=new TextBlock{VerticalAlignment=VerticalAlignment.Center};L(s=>label.Text=s,ko,en);content.Children.Add(label);
        var b=new Button{Content=content,Style=(Style)owner.FindResource(style),MinHeight=36,Padding=new Thickness(13,6,13,6),Margin=new Thickness(8,0,0,0)};
        b.Click+=(_,_)=>click();return b;
    }
    private static Border Chip(TextBlock text,double width=double.NaN){
        text.FontSize=12;text.FontFamily=Mono;text.Foreground=Brush("#C9D1D6");text.VerticalAlignment=VerticalAlignment.Center;text.TextWrapping=TextWrapping.NoWrap;
        text.TextTrimming=TextTrimming.CharacterEllipsis;
        text.SetBinding(ToolTipProperty,new System.Windows.Data.Binding(nameof(TextBlock.Text)){Source=text});
        return new Border{Width=width,Background=Brush("#20282D"),BorderBrush=Line,BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(8),Padding=new Thickness(10,5,10,5),Margin=new Thickness(10,0,0,0),Child=text,VerticalAlignment=VerticalAlignment.Center};
    }
    private void Build(){
        // Top bar: source, mode, signal chips, actions.
        var bar=new Grid();bar.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});bar.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
        var left=new Grid{VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,12,0)};
        left.ColumnDefinitions.Add(new(){Width=new GridLength(40)});left.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});left.ColumnDefinitions.Add(new(){Width=new GridLength(220)});
        left.Children.Add(new Border{Width=32,Height=36,CornerRadius=new CornerRadius(8),Background=Brush("#151B1F"),BorderBrush=Brush("#45535C"),BorderThickness=new Thickness(1),Margin=new Thickness(0,0,8,0),
            Child=new TextBlock{Text="",FontFamily=Icons,FontSize=15,Foreground=Accent,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center}});
        // Selectors have dedicated columns; signal/output metadata belongs to the
        // preview status row, so shrinking the window never wraps toolbar controls.
        deviceBox.Width=double.NaN;deviceBox.MinWidth=0;modeBox.Width=double.NaN;
        deviceBox.ClipToBounds=true;modeBox.ClipToBounds=true;
        Grid.SetColumn(deviceBox,1);Grid.SetColumn(modeBox,2);left.Children.Add(deviceBox);left.Children.Add(modeBox);
        deviceBox.SelectionChanged+=(_,_)=>{if(!updating)DeviceChanged();};
        modeBox.SelectionChanged+=(_,_)=>{if(!updating)ModeChanged();};
        bar.Children.Add(left);
        var actions=new StackPanel{Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center};
        startButton.Style=(Style)owner.FindResource("PrimaryButton");startButton.MinHeight=36;startButton.Padding=new Thickness(16,6,16,6);startButton.Click+=async(_,_)=>await ToggleRunningAsync();
        screenshotButton.Content=null;
        var shot=IconButton("","스크린샷","Screenshot",TakeScreenshot);
        var recordContent=new StackPanel{Orientation=Orientation.Horizontal};recordContent.Children.Add(recordDot);recordContent.Children.Add(recordText);
        recordButton.Content=recordContent;recordButton.Style=(Style)owner.FindResource("SecondaryButton");recordButton.MinHeight=36;recordButton.Padding=new Thickness(13,6,13,6);recordButton.Margin=new Thickness(8,0,0,0);
        // Reserve the translated stop caption and timer even while idle so recording
        // cannot change the header's wrap count or the available preview height.
        recordButton.Width=144;
        recordButton.Click+=(_,_)=>ToggleRecording();
        fullscreenButton.Content="";fullscreenButton.FontFamily=Icons;fullscreenButton.Style=(Style)owner.FindResource("SecondaryButton");fullscreenButton.MinHeight=36;fullscreenButton.Width=40;fullscreenButton.Padding=new Thickness(0);fullscreenButton.Margin=new Thickness(8,0,0,0);
        fullscreenButton.Click+=(_,_)=>SetImmersive(true);
        L(s=>fullscreenButton.ToolTip=s,"전체 화면 (더블클릭)","Full screen (double-click)");
        actions.Children.Add(startButton);actions.Children.Add(shot);actions.Children.Add(recordButton);actions.Children.Add(fullscreenButton);
        Grid.SetColumn(actions,1);bar.Children.Add(actions);
        topBar.Child=bar;topBar.Margin=new Thickness(0,0,0,14);
        screenshotButton.Visibility=Visibility.Collapsed;_=shot;

        // Preview with a placeholder while nothing runs (the native window cannot be overlaid).
        var placeholderStack=new StackPanel{HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center};
        placeholderStack.Children.Add(new Border{Width=58,Height=58,CornerRadius=new CornerRadius(17),Background=Brush("#30271F"),HorizontalAlignment=HorizontalAlignment.Center,
            Child=new TextBlock{Text="",FontFamily=Icons,FontSize=25,Foreground=Accent,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center}});
        placeholderTitle.FontSize=18;placeholderTitle.FontWeight=FontWeights.SemiBold;placeholderTitle.Margin=new Thickness(0,16,0,6);placeholderTitle.TextAlignment=TextAlignment.Center;
        placeholderHint.Foreground=Muted;placeholderHint.TextAlignment=TextAlignment.Center;placeholderHint.MaxWidth=460;placeholderHint.TextWrapping=TextWrapping.Wrap;
        placeholderStack.Children.Add(placeholderTitle);placeholderStack.Children.Add(placeholderHint);
        placeholder.Child=placeholderStack;placeholder.Background=Brush("#0E1113");
        previewArea.Children.Add(host);previewArea.Children.Add(placeholder);
        host.Visibility=Visibility.Collapsed;
        previewFrame.Background=Brush("#000000");previewFrame.CornerRadius=new CornerRadius(0);previewFrame.BorderThickness=new Thickness(0);
        previewFrame.Child=previewArea;previewFrame.ClipToBounds=true;previewFrame.HorizontalAlignment=HorizontalAlignment.Stretch;previewFrame.VerticalAlignment=VerticalAlignment.Stretch;
        previewSpace.Children.Add(previewFrame);previewSpace.SizeChanged+=(_,_)=>FitPreview();
        var liveRow=new Grid{Margin=new Thickness(0,6,0,0)};
        liveRow.ColumnDefinitions.Add(new(){Width=new GridLength(210)});liveRow.ColumnDefinitions.Add(new(){Width=new GridLength(216)});liveRow.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});
        var inputInfo=Chip(signalChip);inputInfo.Margin=new Thickness(0);liveRow.Children.Add(inputInfo);
        var outputInfo=Chip(outputChip);outputInfo.Margin=new Thickness(6,0,0,0);Grid.SetColumn(outputInfo,1);liveRow.Children.Add(outputInfo);
        liveText.Foreground=Muted;liveText.FontSize=12;liveText.FontFamily=Mono;liveText.VerticalAlignment=VerticalAlignment.Center;liveText.TextTrimming=TextTrimming.CharacterEllipsis;liveText.TextWrapping=TextWrapping.NoWrap;
        liveText.Margin=new Thickness(10,0,0,0);Grid.SetColumn(liveText,2);
        liveText.SetBinding(ToolTipProperty,new System.Windows.Data.Binding(nameof(TextBlock.Text)){Source=liveText});
        liveRow.Children.Add(liveText);
        liveBar.Child=liveRow;
        var previewColumn=new Grid();previewColumn.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});previewColumn.RowDefinitions.Add(new(){Height=GridLength.Auto});
        previewColumn.Children.Add(previewSpace);Grid.SetRow(liveBar,1);previewColumn.Children.Add(liveBar);

        // Side panel: tabs and cards.
        BuildTabs();
        var tabsRow=new UniformGrid{Rows=1,Margin=new Thickness(8,8,8,4)};
        foreach(var (key,ko,en) in new[]{("quality","화질","Quality"),("fg","프레임 생성","Frame Gen"),("color","색","Colour"),("audio","오디오","Audio"),("display","표시","Display")}){
            var rb=new RadioButton{Style=(Style)owner.FindResource("TabSegment"),GroupName="CaptureTabs",IsChecked=key==tab};
            L(s=>rb.Content=s,ko,en);rb.Checked+=(_,_)=>{tab=key;tabContent.Content=tabPages[key];UpdatePipeline(last);};
            tabButtons[key]=rb;tabsRow.Children.Add(rb);
        }
        tabContent.Content=tabPages[tab];
        var scroll=new ScrollViewer{Content=tabContent,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,Padding=new Thickness(14,6,8,10)};
        var footer=new Grid{Margin=new Thickness(16,8,16,10)};footer.ColumnDefinitions.Add(new(){Width=GridLength.Auto});footer.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});
        footer.RowDefinitions.Add(new(){Height=GridLength.Auto});footer.RowDefinitions.Add(new(){Height=GridLength.Auto});
        footer.Children.Add(statusDot);
        pathText.FontSize=12;pathText.Foreground=Muted;pathText.TextWrapping=TextWrapping.Wrap;Grid.SetColumn(pathText,1);footer.Children.Add(pathText);
        var side=new Grid();side.RowDefinitions.Add(new(){Height=GridLength.Auto});side.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});side.RowDefinitions.Add(new(){Height=GridLength.Auto});
        side.Children.Add(tabsRow);Grid.SetRow(scroll,1);side.Children.Add(scroll);
        var sep=new Border{Height=1,Background=Line,VerticalAlignment=VerticalAlignment.Top};Grid.SetRow(sep,2);side.Children.Add(sep);Grid.SetRow(footer,2);side.Children.Add(footer);
        sidePanel.Child=side;sidePanel.Background=Surface;sidePanel.CornerRadius=new CornerRadius(12);sidePanel.BorderBrush=Line;sidePanel.BorderThickness=new Thickness(1);

        // Statistics row.
        var stats=new Grid{Margin=new Thickness(0,10,0,0)};
        stats.ColumnDefinitions.Add(new(){Width=new GridLength(1.4,GridUnitType.Star)});stats.ColumnDefinitions.Add(new(){Width=new GridLength(12)});stats.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});
        var signalRows=Rows(("input","입력","Input"),("format","형식","Format"),("content","게임 실제 속도","Game rate"),("output","처리 출력","Processed output"),("audio","오디오","Audio"),("gpu","GPU","GPU"));
        var stagesGrid=Rows(("upload","입력·변환·AA","Input·convert·AA"),("motion","움직임 추정","Motion estimation"),("fg","프레임 생성","Frame generation"),
            ("present","표시(그리기)","Present (draw)"),("encode","녹화 인코딩","Record encode"),("total","합계","Total"));
        var combined=new Grid();combined.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});combined.ColumnDefinitions.Add(new(){Width=new GridLength(20)});combined.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});
        combined.Children.Add(signalRows);Grid.SetColumn(stagesGrid,2);combined.Children.Add(stagesGrid);
        var signal=StatCard("신호와 처리 시간","Signal and processing time",combined);
        var pacing=new StackPanel();
        var fpsLine=new StackPanel{Orientation=Orientation.Horizontal};
        fpsLine.Children.Add(bigFps);
        var outLabel=Label("화면 표시 fps","display fps",12,Muted);outLabel.VerticalAlignment=VerticalAlignment.Bottom;outLabel.Margin=new Thickness(8,0,14,6);fpsLine.Children.Add(outLabel);
        fpsLine.Children.Add(new Border{Width=1,Background=Line,Margin=new Thickness(0,6,14,6)});
        fpsLine.Children.Add(contentFps);
        var contentLabel=Label("게임 실제 fps","game fps",12,Muted);contentLabel.VerticalAlignment=VerticalAlignment.Bottom;contentLabel.Margin=new Thickness(8,0,0,6);fpsLine.Children.Add(contentLabel);
        pacing.Children.Add(fpsLine);
        pacing.Children.Add(Rows(("generated","만든 프레임/초","Generated frames/s")));
        var gauges=new UniformGrid{Rows=1,Margin=new Thickness(0,4,0,0)};
        foreach(var (g,ko,en) in new[]{(processingGauge,"처리 / 프레임 예산","Processing / budget"),(gpuGauge,"GPU 사용률","GPU usage"),(loadGauge,"프레임 예산 사용","Frame budget used")}){
            L(s=>g.Caption=s,ko,en);g.Height=40;g.Margin=new Thickness(0,0,8,0);gauges.Children.Add(g);
        }
        fpsHistory.Height=18;featureText.Height=18;featureText.Margin=new Thickness(0,4,0,0);featureText.TextWrapping=TextWrapping.NoWrap;
        pacing.Children.Add(gauges);pacing.Children.Add(fpsHistory);pacing.Children.Add(featureText);
        featureText.SetBinding(ToolTipProperty,new System.Windows.Data.Binding(nameof(TextBlock.Text)){Source=featureText});
        var pace=StatCard("부드러움","Smoothness",pacing);
        stats.Children.Add(signal);Grid.SetColumn(pace,2);stats.Children.Add(pace);
        statsRow.Child=stats;

        layout.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});layout.ColumnDefinitions.Add(new(){Width=new GridLength(16)});layout.ColumnDefinitions.Add(new(){Width=new GridLength(372)});
        layout.RowDefinitions.Add(new(){Height=GridLength.Auto});layout.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});layout.RowDefinitions.Add(new(){Height=GridLength.Auto});
        Grid.SetColumnSpan(topBar,3);layout.Children.Add(topBar);
        Grid.SetRow(previewColumn,1);layout.Children.Add(previewColumn);
        Grid.SetRow(sidePanel,1);Grid.SetColumn(sidePanel,2);layout.Children.Add(sidePanel);
        Grid.SetRow(statsRow,2);Grid.SetColumnSpan(statsRow,3);statsRow.Height=180;layout.Children.Add(statsRow);
        Content=new Border{Padding=new Thickness(24,14,24,20),Child=layout};
        L(s=>placeholderTitle.Text=s,"캡처 장치를 고르고 미리보기를 시작하세요","Choose a capture device and start the preview");
        L(s=>placeholderHint.Text=Hangul.KeepWords(s),"60Hz 신호 속 30fps 게임 화면의 반복 프레임을 걸러 내고, 그 사이를 실시간으로 채워 부드럽게 보여 줍니다. 다른 프로그램이 장치를 쓰고 있으면 먼저 닫아 주세요.",
            "Repeated frames of a 30 fps game inside a 60 Hz signal are removed and the gaps are filled in real time. Close other programs that use the device first.");
        UpdateRecordButton();UpdateStart();ShowStatus(null);
    }

    private StackPanel Rows(params (string key,string ko,string en)[] rows){
        var stack=new StackPanel();
        foreach(var (key,ko,en) in rows){
            var g=new Grid{Margin=new Thickness(0,3,0,3)};g.ColumnDefinitions.Add(new(){Width=GridLength.Auto});g.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});
            g.Children.Add(Label(ko,en,12.5,Muted));
            var value=new TextBlock{FontFamily=Mono,FontSize=12.5,Foreground=Text,TextAlignment=TextAlignment.Right,TextTrimming=TextTrimming.CharacterEllipsis,TextWrapping=TextWrapping.NoWrap,Margin=new Thickness(12,0,0,0),Text="—"};
            Grid.SetColumn(value,1);g.Children.Add(value);values[key]=value;stack.Children.Add(g);
        }
        return stack;
    }
    private Border StatCard(string ko,string en,UIElement body){
        var stack=new StackPanel();
        stack.Children.Add(Label(ko,en,12.5,Faint));
        if(body is FrameworkElement fe)fe.Margin=new Thickness(0,6,0,0);
        stack.Children.Add(body);
        return new Border{Background=Surface,CornerRadius=new CornerRadius(12),BorderBrush=Line,BorderThickness=new Thickness(1),Padding=new Thickness(12,8,12,8),Child=stack};
    }

    // ---------------------------------------------------------------- option cards
    private ToggleButton Toggle(string key,bool value,Action<bool> changed){
        var t=new ToggleButton{Style=(Style)owner.FindResource("ToggleSwitch"),IsChecked=value,VerticalAlignment=VerticalAlignment.Center};
        t.Checked+=(_,_)=>{if(!updating)changed(true);};t.Unchecked+=(_,_)=>{if(!updating)changed(false);};
        toggles[key]=t;return t;
    }
    private Border Segments(string key,string selected,Action<string> changed,params (string value,string ko,string en)[] items){
        var panel=new StackPanel{Orientation=Orientation.Horizontal};var list=new List<RadioButton>();
        foreach(var (value,ko,en) in items){
            var rb=new RadioButton{Style=(Style)owner.FindResource("Segment"),GroupName="seg-"+key,Tag=value,IsChecked=value==selected};
            L(s=>rb.Content=s,ko,en);rb.Checked+=(_,_)=>{if(!updating)changed(value);};
            panel.Children.Add(rb);list.Add(rb);
        }
        segments[key]=list;
        return new Border{Background=Brush("#151B1F"),BorderBrush=Line,BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(9),Padding=new Thickness(1),Child=panel,HorizontalAlignment=HorizontalAlignment.Left};
    }
    private FrameworkElement OptionRow(string ko,string en,string? helpKo,string? helpEn,UIElement? control,bool stacked=false){
        var grid=new Grid{Margin=new Thickness(0,12,0,0)};
        var text=new StackPanel();text.Children.Add(Label(ko,en,13.5));
        if(helpKo is not null)text.Children.Add(Label(helpKo,helpEn!,12,Faint));
        if(control is null){grid.Children.Add(text);return grid;}
        if(stacked){
            var stack=new StackPanel();stack.Children.Add(text);
            if(control is FrameworkElement f)f.Margin=new Thickness(0,8,0,0);
            stack.Children.Add(control);grid.Children.Add(stack);return grid;
        }
        grid.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});grid.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
        grid.Children.Add(text);
        if(control is FrameworkElement fe){fe.Margin=new Thickness(14,0,0,0);fe.VerticalAlignment=VerticalAlignment.Center;}
        Grid.SetColumn(control,1);grid.Children.Add(control);return grid;
    }
    private Border Card(string glyph,string ko,string en,string subKo,string subEn,ToggleButton? headerToggle,params UIElement[] rows){
        var body=new StackPanel{Margin=new Thickness(0,4,0,2)};foreach(var r in rows)body.Children.Add(r);
        var header=new Grid();header.ColumnDefinitions.Add(new(){Width=GridLength.Auto});header.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});
        header.ColumnDefinitions.Add(new(){Width=GridLength.Auto});header.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
        header.Children.Add(new Border{Width=34,Height=34,CornerRadius=new CornerRadius(9),Background=Brush("#2B231D"),
            Child=new TextBlock{Text=glyph,FontFamily=Icons,FontSize=15,Foreground=Accent,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center}});
        var titles=new StackPanel{Margin=new Thickness(12,0,8,0),VerticalAlignment=VerticalAlignment.Center};
        titles.Children.Add(Label(ko,en,14.5,Text,FontWeights.SemiBold));titles.Children.Add(Label(subKo,subEn,11.5,Faint));
        Grid.SetColumn(titles,1);header.Children.Add(titles);
        if(headerToggle is not null){Grid.SetColumn(headerToggle,2);header.Children.Add(headerToggle);}
        var chevron=new Button{Content="",FontFamily=Icons,FontSize=10,Style=(Style)owner.FindResource("TextButton"),Width=28,Margin=new Thickness(6,0,0,0),VerticalAlignment=VerticalAlignment.Center};
        chevron.Click+=(_,_)=>{bool open=body.Visibility==Visibility.Visible;body.Visibility=open?Visibility.Collapsed:Visibility.Visible;chevron.Content=open?"":"";};
        Grid.SetColumn(chevron,3);header.Children.Add(chevron);
        var stack=new StackPanel();stack.Children.Add(header);stack.Children.Add(body);
        return new Border{Background=Brush("#20272B"),CornerRadius=new CornerRadius(12),BorderBrush=Brush("#2E373C"),BorderThickness=new Thickness(1),Padding=new Thickness(14,12,14,14),Margin=new Thickness(0,0,0,12),Child=stack};
    }
    private readonly WrapPanel pipeline=new();
    private readonly Dictionary<string,(Border chip,TextBlock text,string tab,FrameworkElement? target)> stages=new();
    private readonly TextBlock outputSubtitle=new(),vrrBadgeText=new(),aaSubtitle=new(),aaTime=new(),colorNow=new();
    private readonly Border vrrBadge=new();
    private readonly ComboBox aspectBox=new(){Width=180,Margin=new Thickness(0)},capBox=new(){Width=96,Margin=new Thickness(8,0,0,0)};
    private Border? aaCardRef,fgCardRef,dedupCardRef,colorCardRef;
    // Replaces the second line (subtitle) of a card header with a live text block.
    private static void LiveSubtitle(Border card,TextBlock subtitle){
        if(card.Child is StackPanel stack&&stack.Children[0] is Grid header&&header.Children.OfType<StackPanel>().FirstOrDefault() is {} titles&&titles.Children.Count>1){titles.Children.RemoveAt(1);titles.Children.Add(subtitle);}
    }
    private void BuildTabs(){
        var p=owner.Settings;
        // ---- Frame generation
        L(_=>{bool old=updating;updating=true;var v=options.FrameGeneration=="off"?"auto":options.FrameGeneration;
            methodBox.ItemsSource=new[]{new Choice("auto",T("자동 (권장)","Automatic (recommended)")),new Choice("dlss","NVIDIA DLSS FG (RTX)"),new Choice("vfx","NVIDIA VFX (RTX)"),new Choice("own",T("FrameDoubler 움직임 보간","FrameDoubler motion interpolation"))};
            methodBox.SelectedItem=methodBox.Items.OfType<Choice>().FirstOrDefault(x=>x.Value==v);updating=old;},"","");
        methodBox.HorizontalAlignment=HorizontalAlignment.Left;audioBox.HorizontalAlignment=HorizontalAlignment.Stretch;
        methodBox.SelectionChanged+=(_,_)=>{
            if(updating||methodBox.SelectedItem is not Choice c||options.FrameGeneration=="off")return;
            // NVIDIA VFX is downloaded on request: ask now instead of silently falling back.
            if(c.Value=="vfx"&&!EnsureVfx()){ShowMethod(options.FrameGeneration);return;}
            if(c.Value=="dlss")LiveEngine.ClearDlssBlock();
            SetOptions(options with{FrameGeneration=c.Value},q=>q with{LiveFrameGeneration=c.Value});};
        vfxState.FontSize=12;vfxState.Foreground=Faint;vfxState.TextWrapping=TextWrapping.Wrap;vfxState.Margin=new Thickness(0,6,0,0);
        var fgToggle=Toggle("fg",options.FrameGeneration!="off",on=>{var v=on?(Selected(methodBox)??"auto"):"off";
            if(v=="vfx"&&!EnsureVfx()){v="auto";ShowMethod(v);}
            SetOptions(options with{FrameGeneration=v},q=>q with{LiveFrameGeneration=v});});
        var method=OptionRow("생성 방식","Method","자동은 NVIDIA DLSS 프레임 생성, 내려받은 NVIDIA VFX, FrameDoubler 움직임 보간 순서로 고릅니다. 고른 방식을 GPU나 드라이버가 실행하지 못하면 FrameDoubler 보간으로 바뀝니다.",
            "Automatic picks NVIDIA DLSS Frame Generation, then a downloaded NVIDIA VFX, then FrameDoubler's motion interpolation. When the GPU or driver cannot run the choice, FrameDoubler's interpolation is used.",methodBox,stacked:true);
        var methodStack=new StackPanel();methodStack.Children.Add(method);methodStack.Children.Add(vfxState);
        var multiplier=OptionRow("출력 배율","Multiplier","게임 실제 속도의 2배입니다(30fps 게임은 60fps). 3·4배는 그래픽 드라이버가 DLSS 다중 프레임 생성을 지원하는 GPU(RTX 50 시리즈 등)에서만 표시합니다.",
            "Twice the game's actual frame rate (a 30 fps game becomes 60 fps). 3x and 4x appear only on GPUs whose driver supports DLSS multi-frame generation (such as the RTX 50 series).",
            Segments("multiplier",options.Multiplier.ToString(CultureInfo.InvariantCulture),v=>{int m=int.Parse(v,CultureInfo.InvariantCulture);SetOptions(options with{Multiplier=m},q=>q with{LiveMultiplier=m});},("2","2×","2×"),("3","3×","3×"),("4","4×","4×")),stacked:true);
        var quality=OptionRow("VFX 품질","VFX quality","높을수록 경계가 깨끗하고 GPU를 더 씁니다.","Higher is cleaner at edges and uses more GPU.",
            Segments("vfxQuality",options.VfxQuality.ToString(CultureInfo.InvariantCulture),v=>{int q2=int.Parse(v,CultureInfo.InvariantCulture);SetOptions(options with{VfxQuality=q2},q=>q with{LiveVfxQuality=q2});},
                ("0","성능","Performance"),("1","균형","Balance"),("2","품질","Quality")),stacked:true);
        fgCardRef=Card("","프레임 생성","Frame generation","중복 제거 뒤 빈 자리를 채웁니다","Fills the gaps after deduplication",fgToggle,methodStack,multiplier,quality);
        var dedupToggle=Toggle("dedup",options.Dedup,on=>SetOptions(options with{Dedup=on},q=>q with{LiveDedup=on}));
        contentHint.FontSize=12;contentHint.Foreground=Good;contentHint.Margin=new Thickness(0,10,0,0);
        dedupCardRef=Card("","중복 프레임 제거","Remove duplicate frames","실제로 바뀐 프레임만 남깁니다","Keeps only frames that really changed",dedupToggle,
            OptionRow("자동 감지","Automatic","60Hz 신호에 담긴 30·40fps 게임의 반복 프레임을 화면 전체 비교로 걸러 내고, 실제 화면이 바뀐 시각을 기준으로 보간 위치를 정합니다. 정지 화면에서는 새 프레임을 만들지 않습니다.",
                "Repeats of a 30/40 fps game in a 60 Hz signal are found by comparing whole pictures; interpolation follows the time each picture really changed. Still scenes get no invented frames.",null),contentHint);
        var fgPage=new StackPanel();fgPage.Children.Add(fgCardRef);fgPage.Children.Add(dedupCardRef);tabPages["fg"]=fgPage;

        // ---- Quality: processing order, then anti-aliasing
        var order=new StackPanel();
        var orderHead=new Grid();orderHead.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});orderHead.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
        orderHead.Children.Add(Label("처리 순서","Processing order",12,Faint));
        var locate=Label("누르면 해당 설정으로 이동","Click to locate",11.5,Faint);Grid.SetColumn(locate,1);orderHead.Children.Add(locate);
        order.Children.Add(orderHead);order.Children.Add(pipeline);pipeline.Margin=new Thickness(0,8,0,0);
        var orderCard=new Border{Background=Brush("#171C20"),CornerRadius=new CornerRadius(12),BorderBrush=Line,BorderThickness=new Thickness(1),Padding=new Thickness(14,12,14,10),Margin=new Thickness(0,0,0,12),Child=order};
        var aaToggle=Toggle("aa",options.AntiAlias>0,on=>{int v=on?Math.Max(1,lastAa):0;SetOptions(options with{AntiAlias=v},q=>q with{LiveAntiAlias=v});});
        aaSubtitle.FontSize=11.5;aaSubtitle.Foreground=Faint;
        var methodChip=new TextBlock();L(t=>methodChip.Text=t,"경계 추적","Edge tracing");
        var methodChipBox=Chip(methodChip);methodChipBox.Margin=new Thickness(0);
        aaTime.FontFamily=Mono;aaTime.FontSize=12.5;aaTime.Foreground=Good;
        var where=new TextBlock{FontFamily=Mono,FontSize=12.5,Foreground=Brush("#C9D1D6")};L(t=>where.Text=t,"원본 해상도 · 생성 전","Source size · before FG");
        aaCardRef=Card("","안티앨리어싱","Anti-aliasing","","",aaToggle,
            OptionRow("게임 화면의 계단 현상과 UI 선을 부드럽게 합니다.","Smooths jagged edges in game pictures and interface lines.",
                "고유 프레임마다 원본 해상도에서 한 번 적용한 뒤 프레임 생성에 넘기므로, 생성된 프레임도 함께 깨끗합니다.",
                "Applied once per unique frame at the source resolution and before frame generation, so generated frames are clean too.",null),
            OptionRow("방식","Method","휘도 경계를 찾아 경계 길이만큼 다시 표본화합니다.","Finds luma edges and resamples along their length.",methodChipBox),
            OptionRow("강도","Strength","강할수록 매끄럽고 작은 글자는 조금 흐려집니다.","Stronger is smoother; small text softens slightly.",
                Segments("aa",Math.Max(1,options.AntiAlias).ToString(CultureInfo.InvariantCulture),v=>{lastAa=int.Parse(v,CultureInfo.InvariantCulture);if(options.AntiAlias>0)SetOptions(options with{AntiAlias=lastAa},q=>q with{LiveAntiAlias=lastAa});},
                    ("1","낮음","Low"),("2","보통","Medium"),("3","높음","High")),stacked:true),
            OptionRow("적용 위치","Applied at",null,null,where),OptionRow("입력·변환·AA GPU 시간","Input·convert·AA GPU time",null,null,aaTime));
        LiveSubtitle(aaCardRef,aaSubtitle);
        var qualityPage=new StackPanel();qualityPage.Children.Add(orderCard);qualityPage.Children.Add(aaCardRef);tabPages["quality"]=qualityPage;
        lastAa=Math.Max(1,options.AntiAlias);

        // ---- Colour
        colorNow.FontSize=12;colorNow.Foreground=Good;colorNow.Margin=new Thickness(0,10,0,0);
        colorCardRef=Card("","신호 색","Signal colour","캡처 카드가 보내는 값의 해석","How the card's values are read",null,
            OptionRow("색 범위","Range","대부분의 콘솔·캡처 카드는 제한 범위(16–235)입니다. 검은색이 회색이거나 뭉개질 때만 바꾸세요.",
                "Most consoles and cards send limited range (16–235). Change it only if blacks look grey or crushed.",
                Segments("range",options.Range,v=>SetOptions(options with{Range=v},q=>q with{LiveRange=v}),("auto","자동","Auto"),("limited","제한 16–235","Limited"),("full","전체 0–255","Full")),stacked:true),
            OptionRow("색 행렬","Matrix","HD 이상은 BT.709, SD는 BT.601이 기본입니다.","BT.709 for HD and above, BT.601 for SD by default.",
                Segments("matrix",options.Matrix,v=>SetOptions(options with{Matrix=v},q=>q with{LiveMatrix=v}),("auto","자동","Auto"),("709","BT.709","BT.709"),("601","BT.601","BT.601")),stacked:true),colorNow);
        var hdrBadge=new TextBlock{FontSize=12,Foreground=Warn};L(t=>hdrBadge.Text=t,"SDR만 지원","SDR only");
        var hdrCard=Card("","HDR 신호","HDR signal","10비트 HDR 캡처","10-bit HDR capture",null,
            OptionRow("아직 지원하지 않습니다","Not supported yet","10비트 HDR(P010) 캡처는 아직 처리하지 않습니다. 콘솔이나 캡처 카드에서 SDR 출력을 고르세요.",
                "10-bit HDR (P010) capture is not processed yet. Choose SDR output on the console or capture card.",new Border{Background=Brush("#2A2420"),BorderBrush=Brush("#5A4636"),BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(8),Padding=new Thickness(10,4,10,4),Child=hdrBadge}));
        var colorPage=new StackPanel();colorPage.Children.Add(colorCardRef);colorPage.Children.Add(hdrCard);tabPages["color"]=colorPage;

        // ---- Audio
        audioBox.SelectionChanged+=(_,_)=>{if(!updating&&audioBox.SelectedItem is Choice c){owner.ChangeSettings(q=>q with{CaptureAudio=c.Value});_=RestartAsync();}};
        audioOutputBox.SelectionChanged+=(_,_)=>{if(!updating&&audioOutputBox.SelectedItem is Choice c){owner.ChangeSettings(q=>q with{CaptureAudioOutput=c.Value});session?.SetAudioOutput(c.Value=="default"?"":c.Value);}};
        volume.Value=options.Volume;volumeValue.Text=options.Volume+"%";
        volume.ValueChanged+=(_,_)=>{int v=(int)Math.Round(volume.Value);volumeValue.Text=v+"%";if(!updating)SetOptions(options with{Volume=v},q=>q with{LiveVolume=v});};
        offset.Value=options.AudioOffsetMs;offsetValue.Text=Signed(options.AudioOffsetMs)+" ms";
        offset.ValueChanged+=(_,_)=>{int v=(int)Math.Round(offset.Value);offsetValue.Text=Signed(v)+" ms";if(!updating)SetOptions(options with{AudioOffsetMs=v},q=>q with{LiveAudioOffset=v});};
        var volumeRow=new StackPanel{Orientation=Orientation.Horizontal};volumeRow.Children.Add(volume);volumeRow.Children.Add(volumeValue);
        var offsetRow=new StackPanel{Orientation=Orientation.Horizontal};offsetRow.Children.Add(offset);offsetRow.Children.Add(offsetValue);
        var muteToggle=Toggle("mute",options.Mute,on=>SetOptions(options with{Mute=on},q=>q with{LiveMute=on}));
        var audioCard=Card("","소리","Sound","캡처 카드의 소리를 그대로 들려줍니다","Plays the capture card's sound",null,
            OptionRow("입력","Input","보통 캡처 카드 이름이 들어간 장치입니다. 바꾸면 미리보기를 다시 시작합니다.","Usually the device named after the capture card. Changing it restarts the preview.",audioBox,stacked:true),
            OptionRow("출력","Output","시스템 기본 장치를 따라가거나 특정 장치에 고정합니다. 바꿔도 영상과 녹화는 유지됩니다.","Follow the system default or pin a device. Video and recording continue when changed.",audioOutputBox,stacked:true),
            OptionRow("음량","Volume",null,null,volumeRow,stacked:true),OptionRow("음소거","Mute",null,null,muteToggle),
            OptionRow("소리 지연 보정","Sound delay","화면 처리 지연에 자동으로 맞춘 뒤 더하는 값입니다. 소리가 빠르면 늘리세요.","Added to the automatic match with the picture delay. Increase it if sound is early.",offsetRow,stacked:true));
        var audioPage=new StackPanel();audioPage.Children.Add(audioCard);tabPages["audio"]=audioPage;

        // ---- Display
        outputSubtitle.FontSize=11.5;outputSubtitle.Foreground=Faint;
        var syncRow=OptionRow("화면 동기화","Display sync","자동(권장): 티어링 없이 새로 고침마다 가장 새 프레임을 보여 지연이 가장 적습니다. VSync: 모든 프레임이 새로 고침을 기다립니다. 티어링 허용: 지연이 가장 짧지만 찢김이 보일 수 있습니다.",
            "Auto (recommended): no tearing; each refresh shows the newest frame for the lowest latency. VSync: every frame waits for a refresh. Allow tearing: shortest delay, tear lines may appear.",
            Segments("sync",options.Sync,v=>SetOptions(options with{Sync=v},q=>q with{LiveSync=v}),("auto","자동","Auto"),("vsync","VSync","VSync"),("tearing","티어링 허용","Allow tearing")),stacked:true);
        vrrBadgeText.FontSize=11.5;
        vrrBadge.Child=vrrBadgeText;vrrBadge.CornerRadius=new CornerRadius(8);vrrBadge.Padding=new Thickness(10,4,10,4);vrrBadge.BorderThickness=new Thickness(1);
        var vrrRow=OptionRow("가변 주사율(VRR)","Variable refresh","티어링 허용 출력을 지원하는 모니터에서 전체 화면일 때 드라이버의 G-SYNC·FreeSync가 동작합니다. 연결·주사율·드라이버 설정을 확인하세요.",
            "With a display that accepts tearing output, the driver's G-SYNC/FreeSync works in full screen. Check the connection, refresh rate and driver settings.",vrrBadge);
        capBox.ItemsSource=new[]{"60","90","120","144","165","240"}.Select(x=>new Choice(x,x+" fps")).ToArray();
        string cap=options.OutputCap;
        capBox.SelectedItem=capBox.Items.OfType<Choice>().FirstOrDefault(x=>x.Value==cap)??capBox.Items.OfType<Choice>().ElementAt(2);
        capBox.Visibility=cap is "off" or "display"?Visibility.Collapsed:Visibility.Visible;
        capBox.SelectionChanged+=(_,_)=>{if(!updating&&capBox.SelectedItem is Choice c&&options.OutputCap is not ("off" or "display"))SetOptions(options with{OutputCap=c.Value},q=>q with{LiveOutputCap=c.Value});};
        var capSegments=Segments("cap",cap is "off" or "display"?cap:"custom",v=>{
            string value=v=="custom"?((capBox.SelectedItem as Choice)?.Value??"120"):v;capBox.Visibility=v=="custom"?Visibility.Visible:Visibility.Collapsed;
            SetOptions(options with{OutputCap=value},q=>q with{LiveOutputCap=value});},("off","끄기","Off"),("display","모니터에 맞춤","Follow display"),("custom","직접","Custom"));
        var capLine=new StackPanel{Orientation=Orientation.Horizontal};capLine.Children.Add(capSegments);capLine.Children.Add(capBox);
        var capRow=OptionRow("출력 상한","Output cap","2× 출력이 모니터보다 빠를 때 화면 표시만 제한해 GPU를 아낍니다. 녹화에는 영향이 없습니다.",
            "Limits only what is shown when 2× output outruns the display, saving GPU. Recording is unaffected.",capLine,stacked:true);
        var compareRow=OptionRow("원본 / 처리 비교","Original / processed","좌우 분할은 왼쪽에 캡처 원본, 오른쪽에 처리 결과를 같은 시점으로 보여 줍니다. 녹화에는 영향이 없습니다.",
            "Split shows the captured original on the left and the processed picture on the right at the same moment. Recording is unaffected.",
            Segments("compare","off",v=>{options=options with{Compare=v};session?.Apply(options);ShowStatus(last);},("off","끄기","Off"),("split","좌우 분할","Split"),("original","원본만","Original only")),stacked:true);
        var holdToggle=Toggle("hold",p.LiveHoldOriginal,on=>owner.ChangeSettings(q=>q with{LiveHoldOriginal=on}));
        var holdTitle=new StackPanel();var holdLabel=Label("","",13.5);L(_=>holdLabel.Text=T($"{KeyName(owner.Settings.ShortcutOriginal)} 키를 누르는 동안 원본 보기",$"Hold {KeyName(owner.Settings.ShortcutOriginal)} to see the original"),"","");
        holdTitle.Children.Add(holdLabel);holdTitle.Children.Add(Label("키는 설정 → 단축키에서 바꿀 수 있습니다.","The key can be changed under Settings → Shortcuts.",12,Faint));
        var holdRow=new Grid{Margin=new Thickness(0,12,0,0)};holdRow.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});holdRow.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
        holdRow.Children.Add(holdTitle);holdToggle.Margin=new Thickness(14,0,0,0);Grid.SetColumn(holdToggle,1);holdRow.Children.Add(holdToggle);
        L(_=>{bool old=updating;updating=true;aspectBox.ItemsSource=new[]{new Choice("fit",T("맞춤 (레터박스)","Fit (letterbox)")),new Choice("fill",T("채움 (잘라서)","Fill (crop)")),new Choice("stretch",T("늘림","Stretch"))};
            aspectBox.SelectedItem=aspectBox.Items.OfType<Choice>().FirstOrDefault(x=>x.Value==options.Aspect);updating=old;},"","");
        aspectBox.SelectionChanged+=(_,_)=>{if(!updating&&aspectBox.SelectedItem is Choice c)SetOptions(options with{Aspect=c.Value},q=>q with{LiveAspect=c.Value});};
        var aspectRow=OptionRow("화면 비율","Aspect ratio",null,null,aspectBox);
        var outputCard=Card("","화면 출력","Display output","","",null,syncRow,vrrRow,capRow,compareRow,holdRow,aspectRow);
        LiveSubtitle(outputCard,outputSubtitle);
        var statsToggle=Toggle("stats",p.LiveStats,on=>{owner.ChangeSettings(q=>q with{LiveStats=on});Adapt();});
        var shortcuts=Label("","",12,Faint);
        L(_=>shortcuts.Text=Hangul.KeepWords(ShortcutSummary()),"","");
        var full=new Button{Style=(Style)owner.FindResource("SecondaryButton"),MinHeight=34,Padding=new Thickness(12,4,12,4)};L(s=>full.Content=s,"전체 화면","Full screen");full.Click+=(_,_)=>SetImmersive(true);
        var shortcutButton=new Button{Style=(Style)owner.FindResource("TextButton"),HorizontalAlignment=HorizontalAlignment.Left};L(s=>shortcutButton.Content=s,"단축키 설정…","Shortcut settings…");shortcutButton.Click+=(_,_)=>owner.OpenSettings("shortcuts");
        var viewCard=Card("","보기","View","패널과 단축키","Panels and shortcuts",null,
            OptionRow("통계 패널","Statistics","신호·GPU 단계·프레임 속도 패널을 아래에 표시합니다.","Shows the signal, GPU stage and frame-rate panels below.",statsToggle),
            OptionRow("전체 화면","Full screen","미리보기를 더블클릭해도 됩니다. Esc로 돌아옵니다.","Or double-click the preview. Esc returns.",full),
            OptionRow("단축키","Shortcuts",null,null,shortcuts,stacked:true),shortcutButton);
        var saveText=Label("","",12,Faint);
        L(_=>saveText.Text=T("스크린샷: ","Screenshots: ")+owner.Settings.EffectiveScreenshotDirectory+"\n"+T("녹화: ","Recordings: ")+owner.Settings.EffectiveRecordingDirectory,"","");
        var folders=new WrapPanel{Margin=new Thickness(0,8,0,0)};
        var openShots=new Button{Style=(Style)owner.FindResource("TextButton"),Margin=new Thickness(0,0,14,0)};L(s=>openShots.Content=s,"스크린샷 폴더","Screenshot folder");openShots.Click+=(_,_)=>OpenFolder(owner.Settings.EffectiveScreenshotDirectory);
        var openRecs=new Button{Style=(Style)owner.FindResource("TextButton"),Margin=new Thickness(0,0,14,0)};L(s=>openRecs.Content=s,"녹화 폴더","Recording folder");openRecs.Click+=(_,_)=>OpenFolder(owner.Settings.EffectiveRecordingDirectory);
        var change=new Button{Style=(Style)owner.FindResource("TextButton")};L(s=>change.Content=s,"위치 변경…","Change…");change.Click+=(_,_)=>owner.OpenSettings("capture");
        folders.Children.Add(openShots);folders.Children.Add(openRecs);folders.Children.Add(change);
        saveText.Margin=new Thickness(0,10,0,0);
        var saveCard=Card("","저장 위치","Save locations","설정에서 바꿀 수 있습니다","Changeable in Settings",null,saveText,folders);
        var displayPage=new StackPanel();displayPage.Children.Add(outputCard);displayPage.Children.Add(viewCard);displayPage.Children.Add(saveCard);tabPages["display"]=displayPage;

        // Pipeline chips (built after the cards they point to).
        void Stage(string key,string ko,string en,string tabKey,FrameworkElement? target){
            var text=new TextBlock{FontSize=12};L(t=>text.Text=t,ko,en);
            var chip=new Border{CornerRadius=new CornerRadius(13),BorderThickness=new Thickness(1),Padding=new Thickness(10,3,10,4),Child=text,Cursor=Cursors.Hand,Margin=new Thickness(0,0,0,6)};
            chip.MouseLeftButtonUp+=(_,_)=>{tabButtons[tabKey].IsChecked=true;target?.BringIntoView();};
            if(pipeline.Children.Count>0)pipeline.Children.Add(new TextBlock{Text="→",Foreground=Faint,Margin=new Thickness(6,3,6,0)});
            pipeline.Children.Add(chip);stages[key]=(chip,text,tabKey,target);
        }
        Stage("input","입력","Input","display",null);Stage("color","색 변환","Colour","color",colorCardRef);Stage("aa","안티앨리어싱","Anti-aliasing","quality",aaCardRef);
        Stage("dedup","중복 제거","Deduplication","fg",dedupCardRef);Stage("motion","움직임 추정","Motion estimation","fg",fgCardRef);Stage("fg","프레임 생성","Frame generation","fg",fgCardRef);
        Stage("present","표시 · 녹화","Display · record","display",outputCard);
    }
    private static string KeyName(string shortcut)=>string.IsNullOrEmpty(shortcut)?"—":shortcut;
    private void UpdatePipeline(LiveStatus? s){
        if(stages.Count==0)return;
        bool fg=options.FrameGeneration!="off";bool vfx=s?.FrameGeneration=="vfx"||(s is null&&options.EngineFrameGeneration(LiveEngine.VfxInstalled,LiveEngine.DlssAutomatic)=="vfx");
        bool dlss=s?.FrameGeneration=="dlss"||(s is null&&options.EngineFrameGeneration(LiveEngine.VfxInstalled,LiveEngine.DlssAutomatic)=="dlss");
        var state=new Dictionary<string,bool>{["input"]=true,["color"]=true,["aa"]=options.AntiAlias>0,["dedup"]=options.Dedup,["motion"]=fg&&!vfx,["fg"]=fg,["present"]=true};
        foreach(var (key,(chip,text,_,_)) in stages){
            bool on=state[key];bool focus=(key=="aa"&&tab=="quality")||(key is "fg" or "dedup"&&tab=="fg")||(key=="color"&&tab=="color");
            chip.Background=on&&focus?Brush("#2B231D"):Brushes.Transparent;
            chip.BorderBrush=!on?Line:focus?Accent:Brush("#45535C");
            text.Foreground=on?(focus?Text:Muted):Faint;
            text.TextDecorations=on?null:TextDecorations.Strikethrough;
        }
        stages["fg"].text.Text=vfx&&fg?T("VFX 생성","VFX generation"):dlss&&fg?T("DLSS 생성","DLSS generation"):T("프레임 생성","Frame generation");
    }
    private int lastAa=1;
    private static string Signed(int v)=>v>0?"+"+v:v.ToString(CultureInfo.InvariantCulture);
    private static string? Selected(ComboBox box)=>(box.SelectedItem as Choice)?.Value;
    private string ShortcutSummary(){
        var s=owner.Settings;
        string K(string v)=>string.IsNullOrEmpty(v)?T("없음","none"):v;
        return T($"스크린샷 {K(s.ShortcutScreenshot)} · 녹화 {K(s.ShortcutRecord)} · 전체 화면 {K(s.ShortcutFullscreen)} · 프레임 생성 {K(s.ShortcutFrameGeneration)} · 통계 {K(s.ShortcutStats)}"+(s.GlobalShortcuts?" · 다른 창에서도 동작":""),
            $"Screenshot {K(s.ShortcutScreenshot)} · Record {K(s.ShortcutRecord)} · Full screen {K(s.ShortcutFullscreen)} · Frame generation {K(s.ShortcutFrameGeneration)} · Statistics {K(s.ShortcutStats)}"+(s.GlobalShortcuts?" · also in other windows":""));
    }
    private static void OpenFolder(string path){try{Directory.CreateDirectory(path);Process.Start(new ProcessStartInfo("explorer.exe",'"'+path+'"'){UseShellExecute=true});}catch(Exception){}}

    private void SetOptions(LiveOptions value,Func<Preferences,Preferences> save){
        options=value;session?.Apply(options);owner.ChangeSettings(save);ShowStatus(last);
    }
    private void ResetPage(){
        var d=new LiveOptions();
        updating=true;
        options=tab switch{
            "fg"=>options with{FrameGeneration=d.FrameGeneration,Multiplier=d.Multiplier,VfxQuality=d.VfxQuality,Dedup=d.Dedup},
            "quality"=>options with{AntiAlias=d.AntiAlias},
            "color"=>options with{Range=d.Range,Matrix=d.Matrix},
            "audio"=>options with{Volume=d.Volume,Mute=d.Mute,AudioOffsetMs=d.AudioOffsetMs},
            "display"=>options with{Sync=d.Sync,Aspect=d.Aspect,Compare=d.Compare,OutputCap=d.OutputCap},
            _=>options};
        if(tab=="display")owner.ChangeSettings(q=>q with{LiveStats=true,LiveHoldOriginal=true});
        owner.ChangeSettings(q=>q with{LiveFrameGeneration=options.FrameGeneration,LiveMultiplier=options.Multiplier,LiveVfxQuality=options.VfxQuality,LiveDedup=options.Dedup,
            LiveAntiAlias=options.AntiAlias,LiveRange=options.Range,LiveMatrix=options.Matrix,LiveVolume=options.Volume,LiveMute=options.Mute,LiveAudioOffset=options.AudioOffsetMs,
            LiveSync=options.Sync,LiveAspect=options.Aspect,LiveOutputCap=options.OutputCap});
        SyncControls();updating=false;session?.Apply(options);Adapt();
    }
    private void SyncControls(){
        bool old=updating;updating=true;
        toggles["fg"].IsChecked=options.FrameGeneration!="off";toggles["dedup"].IsChecked=options.Dedup;toggles["aa"].IsChecked=options.AntiAlias>0;
        toggles["mute"].IsChecked=options.Mute;toggles["stats"].IsChecked=owner.Settings.LiveStats;
        void Seg(string key,string value){foreach(var rb in segments[key])rb.IsChecked=(string)rb.Tag==value;}
        Seg("multiplier",options.Multiplier.ToString(CultureInfo.InvariantCulture));Seg("vfxQuality",options.VfxQuality.ToString(CultureInfo.InvariantCulture));
        Seg("aa",Math.Max(1,options.AntiAlias).ToString(CultureInfo.InvariantCulture));Seg("range",options.Range);Seg("matrix",options.Matrix);
        Seg("sync",options.Sync);Seg("compare",options.Compare);Seg("cap",options.OutputCap is "off" or "display"?options.OutputCap:"custom");
        capBox.Visibility=options.OutputCap is "off" or "display"?Visibility.Collapsed:Visibility.Visible;
        aspectBox.SelectedItem=aspectBox.Items.OfType<Choice>().FirstOrDefault(x=>x.Value==options.Aspect);
        toggles["hold"].IsChecked=owner.Settings.LiveHoldOriginal;
        var v=options.FrameGeneration=="off"?"auto":options.FrameGeneration;methodBox.SelectedItem=methodBox.Items.OfType<Choice>().FirstOrDefault(x=>x.Value==v);
        volume.Value=options.Volume;offset.Value=options.AudioOffsetMs;
        updating=old;
    }

    // ---------------------------------------------------------------- devices and session
    public async Task ActivateAsync(){
        if(!LiveEngine.Available){ShowStatus(null);statusMessage=T("실시간 엔진(fdlive.dll)을 찾지 못했습니다.","The live engine (fdlive.dll) was not found.");ShowStatus(null);return;}
        timer.Start();
        if(loaded)return;
        loaded=true;
        await RefreshDevicesAsync();
        _=ProbeMultiplierAsync();
        // Fresh installations also start the selected supported device. Previously only
        // a remembered device started, leaving a first launch on an empty preview.
        if(deviceBox.SelectedItem is DeviceChoice&&modeBox.SelectedItem is ModeChoice)await StartAsync();
    }
    public void Deactivate(){if(!Recording&&!Saving&&!StartingRecording){Stop();}}
    private sealed record DeviceChoice(CaptureDevice Device,string Label){public override string ToString()=>Label;}
    private sealed record ModeChoice(FrameDoubler.Core.CaptureMode Mode,string Label){public override string ToString()=>Label;}
    // Shows 3x/4x only when the driver allows them (see LiveEngine.ProbeMaxMultiplier).
    private bool multiplierProbed;
    private async Task ProbeMultiplierAsync(){
        if(multiplierProbed)return;
        multiplierProbed=true;
        int allowed=await Task.Run(LiveEngine.ProbeMaxMultiplier);
        SetMaxMultiplier(allowed);
    }
    private void SetMaxMultiplier(int allowed){
        maxMultiplier=Math.Clamp(allowed,2,4);
        ShowMultiplierChoices();
        int wanted=Math.Min(owner.Settings.LiveMultiplier,maxMultiplier);
        if(wanted!=options.Multiplier){options=options with{Multiplier=wanted};session?.Apply(options);}
        SyncControls();ShowStatus(last);
    }
    private void ShowMultiplierChoices(){
        foreach(var rb in segments["multiplier"])rb.Visibility=int.Parse((string)rb.Tag,CultureInfo.InvariantCulture)<=maxMultiplier?Visibility.Visible:Visibility.Collapsed;
    }
    private async Task RefreshDevicesAsync(){
        statusMessage=T("캡처 장치를 확인하는 중…","Looking for capture devices…");ShowStatus(last);
        try{inventory=await Task.Run(LiveEngine.Devices);statusMessage="";}
        catch(Exception ex){inventory=null;statusMessage=ex.Message;}
        FillDevices(keepSelection:false);ShowStatus(last);
    }
    private void FillDevices(bool keepSelection){
        if(inventory is null)return;
        bool old=updating;updating=true;
        string? selected=keepSelection?(deviceBox.SelectedItem as DeviceChoice)?.Device.Id:null;
        var devices=inventory.Video.Select(d=>new DeviceChoice(d,T("캡처 카드 · ","Capture card · ")+d.Name)).ToArray();
        deviceBox.ItemsSource=devices;
        deviceBox.SelectedItem=devices.FirstOrDefault(d=>d.Device.Id==selected)??devices.FirstOrDefault(d=>d.Device.Name==owner.Settings.CaptureDevice)
            ??devices.FirstOrDefault(d=>d.Device.Modes.Any(m=>m.Supported))??devices.FirstOrDefault();
        FillModes();FillAudio();
        updating=old;
        if(devices.Length==0)statusMessage=T("캡처 장치가 없습니다. 연결을 확인하세요.","No capture device found. Check the connection.");
    }
    private void FillModes(){
        var device=(deviceBox.SelectedItem as DeviceChoice)?.Device;
        var modes=device?.Modes.Where(m=>m.Supported).OrderByDescending(m=>m.Width*m.Height).ThenByDescending(m=>m.Fps).ThenBy(m=>m.Format=="NV12"?0:1)
            .Select(m=>new ModeChoice(m,m.Label)).ToArray()??[];
        modeBox.ItemsSource=modes;
        modeBox.SelectedItem=modes.FirstOrDefault(m=>m.Mode.Key==owner.Settings.CaptureMode&&device?.Name==owner.Settings.CaptureDevice)
            ??modes.FirstOrDefault(m=>m.Mode==device?.PreferredMode)??modes.FirstOrDefault();
    }
    private void FillAudio(){
        if(inventory is null)return;
        var device=(deviceBox.SelectedItem as DeviceChoice)?.Device;
        var match=device is null?null:inventory.MatchingAudio(device);
        var items=new List<Choice>{new("none",T("사용 안 함","Off"))};
        items.Add(new("auto",T("자동","Automatic")+(match is null?T(" (찾지 못함)"," (not found)"):" · "+match.Name)));
        items.AddRange(inventory.Audio.Select(a=>new Choice(a.Id,a.Name)));
        audioBox.ItemsSource=items;
        audioBox.SelectedItem=items.FirstOrDefault(x=>x.Value==owner.Settings.CaptureAudio)??items[1];
        var outputs=new List<Choice>{new("default",T("시스템 기본 장치","System default device"))};
        outputs.AddRange(inventory.AudioOutputs.Select(a=>new Choice(a.Id,a.Name)));
        string saved=owner.Settings.CaptureAudioOutput;
        if(!outputs.Any(x=>x.Value==saved))outputs.Add(new(saved,T("저장된 장치 (연결 안 됨)","Saved device (disconnected)")));
        audioOutputBox.ItemsSource=outputs;audioOutputBox.SelectedItem=outputs.First(x=>x.Value==saved);
    }
    private string? AudioId(){
        string value=Selected(audioBox)??"auto";
        if(value=="none")return null;
        if(value=="auto"){var device=(deviceBox.SelectedItem as DeviceChoice)?.Device;return device is null||inventory is null?null:inventory.MatchingAudio(device)?.Id;}
        return value;
    }
    private async void DeviceChanged(){bool old=updating;updating=true;FillModes();FillAudio();updating=old;await RestartAsync();}
    private async void ModeChanged()=>await RestartAsync();
    private async Task RestartAsync(){if(session is not null&&!Recording){Stop();await StartAsync();}}
    private async Task ToggleRunningAsync(){if(session is null)await StartAsync();else if(Recording){statusMessage=T("녹화를 먼저 멈추세요.","Stop the recording first.");ShowStatus(last);}else Stop();}
    public async Task StartAsync(){
        if(session is not null||starting)return;
        if(deviceBox.SelectedItem is not DeviceChoice d||modeBox.SelectedItem is not ModeChoice m){statusMessage=T("장치와 모드를 고르세요.","Choose a device and mode.");ShowStatus(last);return;}
        starting=true;UpdateStart();
        host.Visibility=Visibility.Visible;placeholder.Visibility=Visibility.Collapsed;
        await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.Render);   // let the host create its window
        var p=owner.Settings;IntPtr window=host.Handle;string? audio=AudioId();
        captureFailure="";statusMessage=T("시작하는 중…","Starting…");ShowStatus(null);
        try{
            session=await Task.Run(()=>LiveEngine.Open(d.Device,m.Mode,audio,options,p.EffectiveScreenshotDirectory,p.EffectiveRecordingDirectory,p.RecordingCodec,p.RecordingBitrate,window,p.CaptureAudioOutput));
            owner.ChangeSettings(q=>q with{CaptureDevice=d.Device.Name,CaptureMode=m.Mode.Key});
            statusMessage="";fpsHistory.Clear();
            owner.LogCapture(T("미리보기 시작: ","Preview started: ")+d.Device.Name+" · "+m.Mode.Label);
        }catch(Exception ex){
            session=null;statusMessage=ex.Message;host.Visibility=Visibility.Collapsed;placeholder.Visibility=Visibility.Visible;
            owner.LogCapture(T("미리보기 실패: ","Preview failed: ")+ex.Message);
        }finally{starting=false;UpdateStart();UpdateRecordButton();ShowStatus(last);}
    }
    public void Stop(){
        var s=session;session=null;last=null;
        s?.Dispose();
        host.Visibility=Visibility.Collapsed;placeholder.Visibility=Visibility.Visible;
        UpdateStart();UpdateRecordButton();ShowStatus(null);
    }
    // Closing the window: finish a recording and release the device.
    public void Shutdown(){timer.Stop();gpuUsage.Dispose();var s=session;session=null;s?.Dispose();}
    // Offers the NVIDIA VFX download when it is missing; true when it is installed.
    private bool EnsureVfx(){
        if(LiveEngine.VfxInstalled)return true;
        if(!VfxDownload.Run(Window.GetWindow(this),T))return false;
        session?.Command("action=reload-vfx");
        return true;
    }
    private void ShowMethod(string value){
        bool old=updating;updating=true;
        methodBox.SelectedItem=methodBox.Items.OfType<Choice>().FirstOrDefault(x=>x.Value==(value=="off"?"auto":value));
        updating=old;
    }
    public void SettingsChanged(){
        var p=owner.Settings;
        session?.Command($"screenshotDir={p.EffectiveScreenshotDirectory}\nrecordDir={p.EffectiveRecordingDirectory}\nrecordCodec={p.RecordingCodec}\nrecordBitrate={p.RecordingBitrate}");
        // A runtime downloaded in Settings is picked up without restarting the preview.
        if(LiveEngine.VfxInstalled)session?.Command("action=reload-vfx");
        Translate();
    }
    private void TakeScreenshot(){if(session is null){statusMessage=T("미리보기를 먼저 시작하세요.","Start the preview first.");ShowStatus(last);return;}session.Screenshot();}
    private void ToggleRecording(){
        if(session is null){statusMessage=T("미리보기를 먼저 시작하세요.","Start the preview first.");ShowStatus(last);return;}
        if(Saving)return;
        if(Recording||StartingRecording)session.StopRecording();else session.StartRecording();
    }

    // ---------------------------------------------------------------- shortcuts and full screen
    // While the "original" key is held the preview shows the unprocessed capture.
    private bool holding;
    private bool IsOriginalKey(Key key)=>Shortcut.TryParse(owner.Settings.ShortcutOriginal,out _,out var k)&&k==key;
    private void HoldOriginal(bool down){
        if(session is null||!owner.Settings.LiveHoldOriginal||down==holding)return;
        holding=down;session.Apply(down?options with{Compare="original"}:options);
    }
    public bool HandleKeyUp(Key key){if(IsOriginalKey(key)&&holding){HoldOriginal(false);return true;}return false;}
    public bool HandleShortcut(Key key,ModifierKeys modifiers){
        if(key==Key.Escape&&immersive){SetImmersive(false);return true;}
        if(IsOriginalKey(key)&&owner.Settings.LiveHoldOriginal&&session is not null){HoldOriginal(true);return true;}
        var p=owner.Settings;
        string pressed=Shortcut.Format(modifiers,key);
        if(pressed.Length==0)return false;
        if(pressed==p.ShortcutScreenshot){TakeScreenshot();return true;}
        if(pressed==p.ShortcutRecord){ToggleRecording();return true;}
        if(pressed==p.ShortcutFullscreen){SetImmersive(!immersive);return true;}
        if(pressed==p.ShortcutFrameGeneration){toggles["fg"].IsChecked=toggles["fg"].IsChecked!=true;return true;}
        if(pressed==p.ShortcutStats){toggles["stats"].IsChecked=toggles["stats"].IsChecked!=true;return true;}
        return false;
    }
    public void RunShortcut(string action){
        switch(action){case "screenshot":TakeScreenshot();break;case "record":ToggleRecording();break;case "fullscreen":SetImmersive(!immersive);break;
            case "fg":toggles["fg"].IsChecked=toggles["fg"].IsChecked!=true;break;case "stats":toggles["stats"].IsChecked=toggles["stats"].IsChecked!=true;break;}
    }
    public void SetImmersive(bool value){
        if(immersive==value)return;
        immersive=value;Adapt();ImmersiveChanged?.Invoke(value);
    }
    private void Adapt(){
        double scale=.9*owner.UiScale;
        bool stats=owner.Settings.LiveStats&&!immersive&&ActualHeight/Math.Max(.1,owner.UiScale)>560;
        statsRow.Visibility=stats?Visibility.Visible:Visibility.Collapsed;
        topBar.Visibility=sidePanel.Visibility=liveBar.Visibility=immersive?Visibility.Collapsed:Visibility.Visible;
        layout.ColumnDefinitions[1].Width=new GridLength(immersive?0:12*owner.UiScale);layout.ColumnDefinitions[2].Width=new GridLength(immersive?0:1,immersive?GridUnitType.Pixel:GridUnitType.Star);
        // Scale only WPF controls. HwndHost/previewSpace must have no transform.
        foreach(var panel in new FrameworkElement[]{topBar,sidePanel,statsRow,liveBar,placeholder})panel.LayoutTransform=new ScaleTransform(scale,scale);
        if(Content is Border b)b.Padding=immersive?new Thickness(0):new Thickness(16*owner.UiScale,10*owner.UiScale,16*owner.UiScale,12*owner.UiScale);
        previewFrame.CornerRadius=new CornerRadius(0);previewFrame.BorderThickness=new Thickness(0);FitPreview();
    }
    internal void RefreshLayout()=>Adapt();
    private void FitPreview(){
        previewFrame.Width=double.NaN;previewFrame.Height=double.NaN;
        if(immersive){layout.ColumnDefinitions[0].Width=new GridLength(1,GridUnitType.Star);return;}
        // Allocate exactly the picture's width instead of centering a smaller picture
        // inside a wide empty column. The option panel receives the remaining width.
        double available=layout.ActualWidth-layout.ColumnDefinitions[1].ActualWidth-300*.9*owner.UiScale;
        double width=Math.Min(Math.Max(0,available),previewSpace.ActualHeight*16/9);
        double pixels=VisualTreeHelper.GetDpi(this).DpiScaleX;
        width=Math.Round(width*pixels)/pixels;
        if(width>0&&Math.Abs(layout.ColumnDefinitions[0].ActualWidth-width)>.5/pixels)layout.ColumnDefinitions[0].Width=new GridLength(width);
    }

    // ---------------------------------------------------------------- status
    private void UpdateStart(){
        startButton.Content=starting?T("시작하는 중…","Starting…"):session is null?T("미리보기 시작","Start preview"):T("미리보기 중지","Stop preview");
        startButton.IsEnabled=!starting;
    }
    private void UpdateRecordButton(){
        bool rec=Recording;
        recordText.Text=Saving?T("저장 중…","Saving…"):StartingRecording?T("시작 취소","Cancel start"):rec?T("녹화 중지 ","Stop ")+TimeSpan.FromSeconds(Math.Floor(last?.RecordingSeconds??0)).ToString(@"mm\:ss",CultureInfo.InvariantCulture):T("녹화","Record");
        recordDot.Fill=rec?Bad:Brush("#B85050");
        recordButton.BorderBrush=rec?Bad:Brush("#44535D");
        recordButton.IsEnabled=session is not null&&!Saving;
    }
    private void Poll(){
        var s=session;if(s is null){return;}
        LiveStatus? status;
        try{status=s.Status();}catch(Exception ex){statusMessage=ex.Message;return;}
        if(status is null)return;
        last=status;
        foreach(var e in status.Events)HandleEvent(e);
        if(status.CaptureFailed&&!Recording)statusMessage=CaptureFailureMessage();
        if(status.OutputFps>0)fpsHistory.Add(status.OutputFps);
        UpdateRecordButton();ShowStatus(status);
    }
    private string CaptureFailureMessage(){
        if(captureFailure.Contains("0xC00D3704",StringComparison.OrdinalIgnoreCase))
            return T("캡처 시작 실패 (0xC00D3704). 같은 장치를 쓰는 다른 캡처 앱을 닫고 신호·연결을 확인한 뒤 다시 시작하세요.",
                "Capture could not start (0xC00D3704). Close other capture apps using this device, check the signal and connection, then try again.");
        return T("캡처가 멈췄습니다. 신호와 연결을 확인한 뒤 다시 시작하세요.","Capture stopped. Check the signal and connection, then start again.")+
            (captureFailure.Length>0?" · "+captureFailure:"");
    }
    private void HandleEvent(LiveEvent e){
        if(e.Kind=="error"&&e.Message.StartsWith("capture ",StringComparison.OrdinalIgnoreCase))captureFailure=e.Message;
        if(e.Kind=="audio-output"){
            if(e.Message=="ready"){
                if(statusMessage is "오디오 출력을 다시 연결하는 중…" or "Reconnecting audio output…" or "오디오 출력 장치를 확인하세요. 영상은 계속 표시됩니다." or "Check the audio output device. Video continues.")statusMessage="";
            }else statusMessage=e.Message.StartsWith("recovering",StringComparison.Ordinal)?T("오디오 출력을 다시 연결하는 중…","Reconnecting audio output…"):T("오디오 출력 장치를 확인하세요. 영상은 계속 표시됩니다.","Check the audio output device. Video continues.");
            owner.LogCapture("audio output: "+e.Message);return;
        }
        string message=e.Kind switch{
            "screenshot"=>T("스크린샷 저장: ","Screenshot saved: ")+e.Message.Replace("saved ",""),
            "recording" when e.Message.StartsWith("started",StringComparison.Ordinal)=>T("녹화 시작: ","Recording: ")+e.Message[8..],
            "recording"=>T("녹화 저장: ","Recording saved: ")+e.Message.Replace("saved ",""),
            _=>e.Message};
        if(e.Kind is "error" or "warning" or "screenshot" or "recording")statusMessage=message;
        else if(e.Kind=="info"&&statusMessage.StartsWith("NVIDIA VFX",StringComparison.Ordinal)&&
                (message=="NVIDIA VFX frame generation: loading"||message.StartsWith("NVIDIA VFX frame generation ready (",StringComparison.Ordinal)))statusMessage="";
        owner.LogCapture(message);
    }
    private static string Ms(double? v)=>v is {} x?x.ToString("0.0",CultureInfo.InvariantCulture)+" ms":"—";
    private void ShowStatus(LiveStatus? s){
        if(s is {DlssMaxMultiplier:>2}&&s.DlssMaxMultiplier>maxMultiplier){SetMaxMultiplier(s.DlssMaxMultiplier);return;}
        if(s?.FrameGeneration=="dlss"&&statusMessage.StartsWith("DLSS frame generation on this GPU supports up to ",StringComparison.Ordinal))statusMessage="";
        var mode=(modeBox.SelectedItem as ModeChoice)?.Mode;
        string backend=s?.FrameGeneration switch{"dlss"=>"NVIDIA DLSS FG","vfx"=>"NVIDIA VFX","own"=>T("FrameDoubler 보간","FrameDoubler interpolation"),"loading"=>T("프레임 생성 준비 중","Frame generation loading"),"off"=>T("꺼짐","off"),_=>"—"};
        // The engine reports the colour actually used and where it came from (manual, device, default).
        string Source(string? source)=>source switch{"device"=>T(" (장치)"," (device)"),"default"=>T(" (추정)"," (assumed)"),_=>""};
        string range=(s?.ColorRange is {Length:>0} r?r=="full":options.Range=="full")?T("전체","Full"):T("제한","Limited");
        if(mode is not null){
            string p=mode.Height>=2160?"2160p":mode.Height+"p";
            signalChip.Text=$"{p}{mode.Fps:0.##} · {mode.Format} · SDR · {range}";
            double outRate=s?.TargetFps??0;
            string rate=outRate>0?$"{outRate:0.#}fps":options.FrameGeneration=="off"?$"{mode.Fps:0.#}fps":T("감지 중","detecting");
            outputChip.Text=T("출력 ","Output ")+$"{mode.Width}×{mode.Height} · {rate}";
        }else{signalChip.Text=T("신호 없음","No signal");outputChip.Text=T("출력 —","Output —");}
        values["input"].Text=s is null?"—":$"{s.Width}×{s.Height} · {s.InputFps:0.00}";
        values["format"].Text=s is null?"—":$"{s.Format} · {range}{Source(s.ColorRangeSource)} · BT.{(s.ColorMatrix is {Length:>0} m?m:s.Height>=720?"709":"601")}{Source(s.ColorMatrixSource)}";
        values["content"].Text=s is null?"—":options.Dedup?$"{s.ContentFps:0.0} fps"+T(" (중복 제거)"," (deduplicated)"):T("감지 꺼짐","Detection off");
        values["output"].Text=s is null?"—":$"{s.Width}×{s.Height} · {s.OutputFps:0.0}";
        string audioState=s?.AudioOutputState switch{"recovering"=>T("출력 재연결 중","Reconnecting output"),"failed"=>T("출력 연결 안 됨","Output disconnected"),"silent"=>T("무음 · 입력 수신 중","Silent · input receiving"),_=>s?.Audio==true?T("통과 · 지연 ","pass-through · ")+s.AudioDelayMs+" ms":T("꺼짐","off")};
        if(s?.AudioInputState is "stalled" or "failed")audioState=T("오디오 입력 중단","Audio input stopped");
        values["audio"].Text=s is null?"—":audioState;
        audioOutputBox.ToolTip=s?.AudioOutputName is {Length:>0} name?T("사용 중: ","Active: ")+name:null;
        values["gpu"].Text=s is null?"—":s.Gpu;
        values["upload"].Text=Ms(s?.Stages.Upload);values["motion"].Text=Ms(s?.Stages.Motion);values["fg"].Text=Ms(s?.Stages.Interpolate);
        values["present"].Text=Ms(s?.Stages.Present);values["encode"].Text=Ms(s?.Stages.Encode);values["total"].Text=s is null?"—":Ms(s.Stages.Total);
        double target=s?.TargetFps is >0?s.TargetFps:60;
        bigFps.Text=s is null?"—":s.PresentedFps.ToString("0.0",CultureInfo.InvariantCulture);
        bigFps.Foreground=s is null?Text:s.PresentedFps>=target*0.97?Good:s.PresentedFps>=target*0.85?Warn:Bad;
        contentFps.Text=s is null||!options.Dedup?"—":s.ContentFps.ToString("0.0",CultureInfo.InvariantCulture);
        values["generated"].Text=s is null?"—":s.InterpolatedFps.ToString("0.0",CultureInfo.InvariantCulture);
        double budget=1000/Math.Max(1,target);
        processingGauge.Set(s is null?0:s.Stages.Total/budget,s is null?"—":$"{s.Stages.Total:0.0} / {budget:0.0}","ms");
        gpuUsage.SelectAdapter(s?.GpuLuid??"");
        double? gpuPercent=s is null?null:gpuUsage.Percent;
        gpuGauge.Set((gpuPercent??0)/100,gpuPercent?.ToString("0",CultureInfo.InvariantCulture)??"—","%");
        gpuGauge.ToolTip=T("선택한 GPU 전체의 가장 바쁜 엔진 사용률입니다. 약 1초마다 갱신하며 미측정은 —로 표시합니다.","Busiest engine on the selected GPU across all processes. Updated about once a second; — means unavailable.");
        loadGauge.Set(s is null?0:s.Stages.Total/budget,s is null?"—":(s.Stages.Total/budget*100).ToString("0",CultureInfo.InvariantCulture),"%");
        var parts=new List<string>();
        if(options.Dedup)parts.Add(T("자동 중복 제거","Automatic dedup")+(s is null?"":T($" · 콘텐츠 약 {s.ContentFps:0}fps",$" · content about {s.ContentFps:0} fps")));
        if(options.FrameGeneration!="off")parts.Add(T("프레임 생성 ","Frame generation ")+$"{options.Multiplier}× · {backend}");
        if(options.AntiAlias>0)parts.Add(T("안티앨리어싱 ","Anti-aliasing ")+(options.AntiAlias>1?T("강","high"):T("약","low")));
        featureText.Text=Hangul.KeepWords(T("사용 중: ","Enabled: ")+(parts.Count==0?T("없음","none"):string.Join("; ",parts)));
        contentHint.Text=s is null?"":options.Dedup?T($"감지된 콘텐츠: 약 {s.ContentFps:0.0}fps · 중복 {s.Duplicates:N0}장 제거",$"Detected content: about {s.ContentFps:0.0} fps · {s.Duplicates:N0} repeats removed")
            :T("중복 프레임 제거를 켜면 게임의 실제 속도를 감지합니다.","Enable duplicate removal to detect the game's actual rate.");
        bool vfx=LiveEngine.VfxInstalled;
        vfxState.Text=Hangul.KeepWords(LiveEngine.DlssBlocked?T("지난번 DLSS 프레임 생성 중 프로그램이 비정상 종료되어 자동 선택에서 뺐습니다. 생성 방식에서 NVIDIA DLSS FG를 직접 고르면 다시 시도합니다.","The program ended abnormally while DLSS Frame Generation was in use, so Automatic no longer picks it. Select NVIDIA DLSS FG as the method to try again."):s?.FrameGeneration=="dlss"?T("NVIDIA DLSS 프레임 생성 사용 중","NVIDIA DLSS Frame Generation active")+(s.VfxRuntime is {Length:>0} d?" · "+d:""):vfx?T("NVIDIA VFX 런타임 설치됨","NVIDIA VFX runtime installed")+(s?.VfxRuntime is {Length:>0} v?" · "+v:"")+(s?.FrameGeneration=="vfx"?T(" · 사용 중"," · active"):"")
            :T("NVIDIA VFX 런타임은 설정 › 캡처에서 받을 수 있습니다.","The NVIDIA VFX runtime can be downloaded in Settings › Capture."));
        if(s is {DlssMaxMultiplier:>0,FrameGeneration:"own"}&&(options.FrameGeneration is "auto" or "dlss")&&options.Multiplier>s.DlssMaxMultiplier)
            vfxState.Text=Hangul.KeepWords(T($"이 드라이버의 DLSS는 최대 {s.DlssMaxMultiplier}×입니다. 선택한 {options.Multiplier}×는 FrameDoubler 보간으로 처리합니다.",$"This driver supports DLSS up to {s.DlssMaxMultiplier}x. The selected {options.Multiplier}x uses FrameDoubler interpolation."));
        string[] aaNames={T("낮음","low"),T("보통","medium"),T("높음","high")};
        aaSubtitle.Text=options.AntiAlias>0?T("경계 추적 AA · ","Edge-tracing AA · ")+aaNames[Math.Clamp(options.AntiAlias,1,3)-1]:T("꺼짐","Off");
        aaTime.Text=Ms(s?.Stages.Upload);
        int height=s?.Height??mode?.Height??1080;
        colorNow.Text=T("현재: ","Now: ")+(options.Range=="full"?T("전체 범위","full range"):T("제한 범위","limited range"))+" · "+(options.Matrix=="601"||(options.Matrix=="auto"&&height<720)?"BT.601":"BT.709")+" · SDR";
        int hz=s?.DisplayHz??0;bool tear=s?.Tearing??false;
        outputSubtitle.Text=(hz>0?T($"모니터 {hz}Hz",$"Display {hz} Hz"):T("모니터 정보는 미리보기 중 표시","Display shown while previewing"))+(s is null?"":" · "+(tear?T("티어링 허용 출력 지원","tearing output supported"):T("티어링 허용 출력 미지원","no tearing output")));
        vrrBadgeText.Text=s is null?T("미확인","unknown"):tear?T("사용 가능","available"):T("사용할 수 없음","unavailable");
        vrrBadgeText.Foreground=s is null?Muted:tear?Good:Warn;
        vrrBadge.Background=s is null?Brush("#20282D"):tear?Brush("#1D2A22"):Brush("#2A2420");
        vrrBadge.BorderBrush=s is null?Line:tear?Brush("#2F5A40"):Brush("#5A4636");
        if(segments.TryGetValue("sync",out var syncButtons))foreach(var rb in syncButtons)if((string)rb.Tag=="tearing")rb.IsEnabled=s is null||tear;
        UpdatePipeline(s);
        if(s is null){
            liveText.Text=statusMessage.Length>0?statusMessage:T("대기 중","Idle");liveText.Foreground=statusMessage.Length>0?Warn:Muted;
            statusDot.Fill=Faint;pathText.Text=Hangul.KeepWords(statusMessage.Length>0?statusMessage:T("미리보기가 꺼져 있습니다","Preview is off"));
        }else{
            string rec=s.Recording?T($" · ● 녹화 {TimeSpan.FromSeconds(Math.Floor(s.RecordingSeconds)):mm\\:ss}",$" · ● REC {TimeSpan.FromSeconds(Math.Floor(s.RecordingSeconds)):mm\\:ss}"):"";
            liveText.Text=(statusMessage.Length>0?statusMessage+"  |  ":"")+T("● 실시간","● LIVE")+" · "+T("지연","latency")+$" {(s.LatencyMs is {} lt?lt.ToString("0",CultureInfo.InvariantCulture):"—")} ms"+rec;
            if(s.CaptureState=="waiting"&&statusMessage.Length==0)liveText.Text=T("영상 입력을 기다리는 중…","Waiting for video input…");
            liveText.Foreground=statusMessage.Length>0?Warn:Muted;
            statusDot.Fill=s.CaptureFailed?Bad:Good;
            string motion=options.FrameGeneration=="off"?T("없음","none"):s.FrameGeneration=="vfx"?"NVIDIA VFX":s.FrameGeneration=="dlss"&&s.DlssMotion=="nvof"?"NVIDIA Optical Flow":s.FrameGeneration=="dlss"&&s.DlssMotion=="none"?"NVIDIA DLSS":$"{s.Width/2}×{s.Height/2}";
            pathText.Text=Hangul.KeepWords(T("실시간 캡처 | 입력 ","Live capture | input ")+$"{s.Width}×{s.Height} / "+T("움직임 추정 ","motion ")+motion+" / "+T("출력 ","output ")+$"{s.Width}×{s.Height} | "+T("GPU에서 실시간 처리","processed on the GPU in real time"));
        }
    }
}

// The child window the engine presents into. Double-clicks toggle full screen and keys
// are forwarded, since the native window would otherwise swallow them.
internal sealed class PreviewHost:HwndHost
{
    private const int WS_CHILD=0x40000000,WS_VISIBLE=0x10000000,WS_CLIPSIBLINGS=0x04000000,WS_CLIPCHILDREN=0x02000000,CS_DBLCLKS=0x0008,CS_HREDRAW=2,CS_VREDRAW=1;
    private const int WM_LBUTTONDBLCLK=0x0203,WM_KEYDOWN=0x0100,WM_SYSKEYDOWN=0x0104,WM_ERASEBKGND=0x0014;
    private const string ClassName="FrameDoublerPreview";
    private static bool registered;
    private IntPtr window;
    public event Action? DoubleClicked;
    public event Func<Key,ModifierKeys,bool>? KeyPressed;
    public event Func<Key,bool>? KeyReleased;
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
    private struct WNDCLASSEX{public int cbSize;public int style;public IntPtr lpfnWndProc;public int cbClsExtra;public int cbWndExtra;public IntPtr hInstance;public IntPtr hIcon;public IntPtr hCursor;public IntPtr hbrBackground;public string? lpszMenuName;public string lpszClassName;public IntPtr hIconSm;}
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern IntPtr CreateWindowEx(int ex,string cls,string name,int style,int x,int y,int w,int h,IntPtr parent,IntPtr menu,IntPtr instance,IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("kernel32.dll",CharSet=CharSet.Ansi)] private static extern IntPtr GetProcAddress(IntPtr module,string name);
    [DllImport("gdi32.dll")] private static extern IntPtr GetStockObject(int index);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern IntPtr LoadCursor(IntPtr instance,IntPtr name);
    protected override HandleRef BuildWindowCore(HandleRef parent){
        if(!registered){
            var wc=new WNDCLASSEX{cbSize=Marshal.SizeOf<WNDCLASSEX>(),style=CS_DBLCLKS|CS_HREDRAW|CS_VREDRAW,lpfnWndProc=GetProcAddress(GetModuleHandle("user32.dll"),"DefWindowProcW"),
                hInstance=GetModuleHandle(null),hCursor=LoadCursor(IntPtr.Zero,32512),hbrBackground=GetStockObject(4),lpszClassName=ClassName};
            RegisterClassEx(ref wc);registered=true;
        }
        window=CreateWindowEx(0,ClassName,"",WS_CHILD|WS_VISIBLE|WS_CLIPSIBLINGS|WS_CLIPCHILDREN,0,0,Math.Max(1,(int)ActualWidth),Math.Max(1,(int)ActualHeight),parent.Handle,IntPtr.Zero,GetModuleHandle(null),IntPtr.Zero);
        return new HandleRef(this,window);
    }
    protected override void DestroyWindowCore(HandleRef hwnd){DestroyWindow(hwnd.Handle);window=IntPtr.Zero;}
    protected override IntPtr WndProc(IntPtr hwnd,int msg,IntPtr wParam,IntPtr lParam,ref bool handled){
        switch(msg){
            case WM_LBUTTONDBLCLK:DoubleClicked?.Invoke();handled=true;return IntPtr.Zero;
            case WM_ERASEBKGND:handled=true;return new IntPtr(1);
            case WM_KEYDOWN or WM_SYSKEYDOWN:
                if((lParam.ToInt64()&0x40000000)!=0)break;   // auto-repeat
                var key=KeyInterop.KeyFromVirtualKey(wParam.ToInt32());
                if(KeyPressed?.Invoke(key,Keyboard.Modifiers)==true){handled=true;return IntPtr.Zero;}
                break;
            case 0x0101 or 0x0105:   // WM_KEYUP, WM_SYSKEYUP
                if(KeyReleased?.Invoke(KeyInterop.KeyFromVirtualKey(wParam.ToInt32()))==true){handled=true;return IntPtr.Zero;}
                break;
        }
        return base.WndProc(hwnd,msg,wParam,lParam,ref handled);
    }
}

// Live budget bars keep the measured value visible even when it exceeds the scale.
internal sealed class BudgetBar:FrameworkElement
{
    private double value;private string text="—",unit="";
    public string Caption{get;set;}="";
    private static readonly Brush Track=Frozen("#2B3338"),Ok=Frozen("#5BD38A"),Mid=Frozen("#F4BD83"),High=Frozen("#FF6B6B"),Label=Frozen("#F3F1EB"),Small=Frozen("#A6B1B8");
    private static Brush Frozen(string c){var b=(SolidColorBrush)new BrushConverter().ConvertFromString(c)!;b.Freeze();return b;}
    public void Set(double v,string t,string u){value=double.IsFinite(v)?Math.Clamp(v,0,1):0;text=t;unit=u;InvalidateVisual();}
    protected override void OnRender(DrawingContext dc){
        if(ActualWidth<1)return;
        double dpi=VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var main=new FormattedText(text+" "+unit,CultureInfo.InvariantCulture,FlowDirection.LeftToRight,new Typeface("Cascadia Mono, Consolas"),11,Label,dpi);
        var sub=new FormattedText(Caption,CultureInfo.InvariantCulture,FlowDirection.LeftToRight,new Typeface("Segoe UI"),10,Small,dpi);
        sub.MaxTextWidth=Math.Max(1,ActualWidth);sub.Trimming=TextTrimming.CharacterEllipsis;
        main.MaxTextWidth=Math.Max(1,ActualWidth);main.Trimming=TextTrimming.CharacterEllipsis;
        dc.DrawText(main,new Point(0,0));dc.DrawText(sub,new Point(0,16));
        dc.DrawRoundedRectangle(Track,null,new Rect(0,34,ActualWidth,3),1.5,1.5);
        if(value>0)dc.DrawRoundedRectangle(value<0.6?Ok:value<0.9?Mid:High,null,new Rect(0,34,ActualWidth*value,3),1.5,1.5);
    }
}

// Output frame rate over the last minute: steadiness at a glance.
internal sealed class Sparkline:FrameworkElement
{
    private readonly Queue<double> points=new();
    private static readonly Pen Stroke=new(new SolidColorBrush(Color.FromRgb(0x5B,0xD3,0x8A)),1.5);
    private static readonly Brush Fill=new SolidColorBrush(Color.FromArgb(0x30,0x5B,0xD3,0x8A));
    static Sparkline(){Stroke.Freeze();Fill.Freeze();}
    public void Add(double v){points.Enqueue(v);while(points.Count>240)points.Dequeue();InvalidateVisual();}
    public void Clear(){points.Clear();InvalidateVisual();}
    protected override void OnRender(DrawingContext dc){
        if(points.Count<2||ActualWidth<4)return;
        double max=Math.Max(1,points.Max()*1.08),min=Math.Max(0,points.Min()*0.9);if(max-min<1)min=max-1;
        var list=points.ToArray();double step=ActualWidth/(list.Length-1);
        var g=new StreamGeometry();
        using(var ctx=g.Open()){
            ctx.BeginFigure(new Point(0,ActualHeight),true,true);
            for(int i=0;i<list.Length;i++)ctx.LineTo(new Point(i*step,ActualHeight-(list[i]-min)/(max-min)*(ActualHeight-2)),true,false);
            ctx.LineTo(new Point(ActualWidth,ActualHeight),false,false);
        }
        g.Freeze();dc.DrawGeometry(Fill,null,g);
        var line=new StreamGeometry();
        using(var ctx=line.Open()){
            ctx.BeginFigure(new Point(0,ActualHeight-(list[0]-min)/(max-min)*(ActualHeight-2)),false,false);
            for(int i=1;i<list.Length;i++)ctx.LineTo(new Point(i*step,ActualHeight-(list[i]-min)/(max-min)*(ActualHeight-2)),true,false);
        }
        line.Freeze();dc.DrawGeometry(null,Stroke,line);
    }
}

// "Ctrl+Shift+F7"-style key combinations for the capture shortcuts.
internal static class Shortcut
{
    public static bool IsModifier(Key key)=>key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System;
    public static string Format(ModifierKeys modifiers,Key key){
        if(key==Key.None||IsModifier(key))return "";
        var parts=new List<string>();
        if(modifiers.HasFlag(ModifierKeys.Control))parts.Add("Ctrl");if(modifiers.HasFlag(ModifierKeys.Alt))parts.Add("Alt");
        if(modifiers.HasFlag(ModifierKeys.Shift))parts.Add("Shift");if(modifiers.HasFlag(ModifierKeys.Windows))parts.Add("Win");
        parts.Add(key.ToString());
        return string.Join("+",parts);
    }
    public static bool TryParse(string text,out ModifierKeys modifiers,out Key key){
        modifiers=ModifierKeys.None;key=Key.None;
        if(string.IsNullOrWhiteSpace(text))return false;
        foreach(var part in text.Split('+',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries)){
            switch(part){case "Ctrl":modifiers|=ModifierKeys.Control;break;case "Alt":modifiers|=ModifierKeys.Alt;break;case "Shift":modifiers|=ModifierKeys.Shift;break;case "Win":modifiers|=ModifierKeys.Windows;break;
                default:if(!Enum.TryParse(part,out key))return false;break;}
        }
        return key!=Key.None;
    }
}
