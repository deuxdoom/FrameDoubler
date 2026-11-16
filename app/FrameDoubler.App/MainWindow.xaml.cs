using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FrameDoubler.Core;
using Microsoft.Win32;
using Forms=System.Windows.Forms;

namespace FrameDoubler.App;

public sealed record Choice(string Value,string Label){public override string ToString()=>Label;}
public sealed class QueueEntry(string input,Func<string,string,string> translate):INotifyPropertyChanged
{
    public string Input{get;}=input;
    public string Name=>Path.GetFileName(Input);
    public AnalysisResult? Analysis{get;set;}
    public string? Output{get;set;}
    public ConversionResult? Result{get;set;}
    public TimeEstimate? Estimate{get;set;}
    public ImageSource? Thumbnail{get;set;}
    // 1-based position in the queue; batch conversion follows this order.
    public int Number{get;set;}
    public string State{get;set;}="pending";
    public string Metadata{get{if(Analysis is not { } a)return "";string motion=a.MotionFps is { } fps?$"~{fps:0.##}fps":translate("추정 불가","not estimated");
        string range=a.Source.HdrInput is { } hdr?$"{hdr.BitDepth}-bit HDR({hdr.Name}) → SDR":$"{a.Source.BitDepth}-bit SDR";
        return $"{a.Source.Width} × {a.Source.Height} · {range} · {translate("파일","File")} {a.Source.Fps.ToDouble():0.##}fps · {translate("움직임","Motion")} {motion}";}}
    public string Details{get{if(Analysis is not {} a)return "";var s=a.Source;long? rate=s.VideoBitrate??s.OverallBitrate;
        long seconds=(long)s.Duration.ToDouble();string length=$"{seconds/3600:00}:{seconds/60%60:00}:{seconds%60:00}";
        return $"{s.Codec.ToUpperInvariant()} · "+(rate is {} b?$"{(s.VideoBitrate is null?translate("전체 평균 ","Overall average "):"")}{b/1_000_000d:0.##} Mbps":translate("비트레이트 정보 없음","Bitrate unavailable"))+$" · {length}"+(a.IsSampled?translate(" · 구간 분석"," · Sampled"):"");}}
    public string Status=>State switch{"analyze"=>translate("분석 중","Analyzing"),"ready"=>translate("변환 준비","Ready"),"convert"=>translate("변환 중","Processing"),
        "done"=>translate("구간 검증 통과","Sample verification passed"),"unverified"=>translate("변환 완료 · 검증 안 함","Converted · not verified"),"warning"=>translate("검증 경고 · 결과 확인 필요","Quality warning · review output"),
        "error"=>translate("오류 · 다시 시도 가능","Error · retry available"),"cancelled"=>translate("취소됨","Cancelled"),_=>translate("대기","Pending")};
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Refresh(){PropertyChanged?.Invoke(this,new(null));}
}

public partial class MainWindow:Window
{
    private static readonly AppPaths paths=AppPaths.Discover();
    private readonly ConversionPipeline pipeline=new(paths);
    public ObservableCollection<QueueEntry> Entries{get;}=new();
    private HardwareInventory? inventory;
    private List<EncoderCapability> encoders=new();
    private Preferences preferences=Preferences.Load();
    private CancellationTokenSource? work;
    private bool busy,ready,updating,closing;
    private WindowState restoreState=WindowState.Normal;
    private Forms.NotifyIcon? tray;
    private readonly CancellationTokenSource lifetime=new();
    private UpdateRelease? availableRelease;
    internal Func<string[]?>? FilePicker{get;set;}
    private string language="ko";
    public bool English=>language=="en";
    public bool HardwareReady=>ready;
    public void SelfTestOutputFolder(string path)=>OutputBox.Text=path;
    // Saved before the installer copies settings into the new version; a failure stops the update
    // (exit is approved separately, only after the installer has confirmed the handoff).
    internal void SaveForUpdate()=>CurrentPreferences().Save();
    private string T(string ko,string en)=>English?en:ko;
    // Same rule as the pipeline (digits with an optional k/M); a typo is not saved over the last good value.
    internal static bool ValidBitrate(string? value)=>string.IsNullOrEmpty(value)||System.Text.RegularExpressions.Regex.IsMatch(value,@"^\d+[kKmM]?$");
    private static string Selected(ComboBox box)=>(box.SelectedItem as Choice)?.Value??"auto";
    private static void Select(ComboBox box,string value)=>box.SelectedItem=box.Items.OfType<Choice>().FirstOrDefault(x=>x.Value==value)??box.Items.OfType<Choice>().FirstOrDefault();
    private void Fill(ComboBox box,IEnumerable<Choice> choices,string? value=null){string selected=value??Selected(box);box.ItemsSource=choices.ToArray();Select(box,selected);}
    public MainWindow(){
        InitializeComponent();Style=(Style)FindResource(typeof(Window));QueueList.ItemsSource=Entries;Entries.CollectionChanged+=(_,_)=>Renumber();
        PreviewKeyDown+=ConvertKeyDown;QueueList.MouseDoubleClick+=QueueDoubleClicked;
        updating=true;SetLanguage(preferences.Language,false);LanguageThumb.SizeChanged+=(_,_)=>MoveLanguageThumb(false);
        Fill(ModelBox,[new("v4.25","RIFE 4.25"),new("v4.26","RIFE 4.26")],preferences.Model);
        // Empty means "beside each source"; say so in the box instead of only in its tooltip.
        OutputBox.TextChanged+=(_,_)=>OutputHint.Visibility=OutputBox.Text.Length==0?Visibility.Visible:Visibility.Collapsed;
        OutputBox.Text=preferences.OutputDirectory;
        OutputHint.Visibility=OutputBox.Text.Length==0?Visibility.Visible:Visibility.Collapsed;
        HardwareCheck.IsChecked=preferences.HardwareEncoding;VerifyCheck.IsChecked=preferences.Verify;
        Translate();Select(PresetBox,preferences.Preset);Select(FpsBox,preferences.Fps);updating=false;
        StateChanged+=(_,_)=>{if(WindowState==WindowState.Maximized&&!immersive){WindowState=WindowState.Normal;FitFixedWindow(false);return;}WindowBorder.CornerRadius=new(12);
            if(WindowState!=WindowState.Minimized)restoreState=WindowState;
            if(WindowState==WindowState.Minimized&&preferences.MinimizeAction=="tray")HideToTray();};
        SizeChanged+=(_,_)=>{UpdateCompactLayout();AdaptLayout(ActualHeight/Math.Max(.1,UiScale));};
        DragLeave+=(_,_)=>DropBorder.BorderBrush=(Brush)new BrushConverter().ConvertFromString("#3A454C")!;
        foreach(var box in new[]{FpsBox,ModelBox,DeviceBox,EncoderBox})box.SelectionChanged+=(_,_)=>{if(!updating)ScheduleEstimate();};
        VerifyCheck.Checked+=(_,_)=>{if(!updating)ScheduleEstimate();};VerifyCheck.Unchecked+=(_,_)=>{if(!updating)ScheduleEstimate();};
        HudBox.TextChanged+=(_,_)=>{if(!updating)ScheduleEstimate();};
        InitializeCapture();
    }
    protected override void OnSourceInitialized(EventArgs e){base.OnSourceInitialized(e);WindowFrame.Attach(this);InitializeFixedWindow();}
    private void MinimizeClicked(object sender,RoutedEventArgs e){if(preferences.MinimizeAction=="tray")HideToTray();else WindowState=WindowState.Minimized;}
    private void CloseClicked(object sender,RoutedEventArgs e)=>RequestExit(ExitTrigger.Close);
    public enum ExitTrigger{Close,Tray}
    // Test seam, like FilePicker: answers the exit/tray confirmation without showing the dialog.
    internal Func<ExitTrigger,DialogChoice>? ExitPrompt{get;set;}
    private bool exitConfirmed,asking;
    // Programmatic exits (update install, diagnostics, Windows sign-out) skip the confirmation.
    internal void AllowExit()=>exitConfirmed=true;
    // Just a title and two buttons; tray/exit behaviour itself is chosen in Settings → Window and tray.
    internal BrandDialog CreateExitDialog(ExitTrigger trigger){
        var dialog=new BrandDialog(this,"FrameDoubler",400);
        var cancel=new DialogButton(T("취소","Cancel"),DialogChoice.Cancel,"TextButton");
        dialog.Configure(BrandDialog.Glyphs.Exit,CaptureRecording?T("녹화를 저장하고 종료할까요?","Save the recording and exit?"):busy||measuring?T("진행 중인 작업을 취소하고 종료할까요?","Cancel the running job and exit?"):T("FrameDoubler를 종료할까요?","Exit FrameDoubler?"),
            "",null,cancel,new DialogButton(T("종료","Exit"),DialogChoice.Primary,"PrimaryButton",true));
        return dialog;
    }
    private void RequestExit(ExitTrigger trigger){
        if(asking||exitConfirmed)return;
        bool working=busy||measuring||CaptureRecording;
        // A remembered choice (Settings → Window and tray) skips the dialog; exiting while work runs is always confirmed.
        string remembered=trigger==ExitTrigger.Close?preferences.CloseAction:preferences.CloseAction=="exit"?"exit":"ask";
        if(ExitPrompt is null){
            if(remembered=="tray"&&trigger!=ExitTrigger.Tray){HideToTray();return;}
            if(remembered=="exit"&&!working){exitConfirmed=true;Close();return;}
        }
        asking=true;
        try{
            DialogChoice choice;
            if(ExitPrompt is not null)choice=ExitPrompt(trigger);
            else{var dialog=CreateExitDialog(trigger);dialog.ShowDialog();choice=dialog.Choice;}
            if(choice==DialogChoice.Primary){exitConfirmed=true;Close();}
        }finally{asking=false;}
    }
    private void HideToTray(){if(tray is null){WindowState=WindowState.Minimized;return;}Hide();}
    internal void AdaptLayout(double height){
        MetricsPanel.Visibility=height<660?Visibility.Collapsed:Visibility.Visible;Details.Visibility=height<600?Visibility.Collapsed:Visibility.Visible;
        DropIcon.Visibility=height<600?Visibility.Collapsed:Visibility.Visible;DropText.Margin=height<600?new(0,0,0,6):new(0,18,0,8);
    }
    public async Task InitializeAsync(bool useTray=true){
        // The tray icon used to appear only after device/encoder detection (several seconds of
        // Vulkan and encoder test runs). It needs none of that, so it is created immediately.
        if(useTray)SetupTray();
        try{
            var data=await Task.Run(()=>pipeline.HardwareAsync(lifetime.Token));inventory=data.Hardware;encoders=data.Encoders;
            updating=true;RefreshOptions();Select(DeviceBox,preferences.Device);
            if(Selected(DeviceBox)!="auto"&&Selected(DeviceBox)!="cpu"&&preferences.DeviceName!=((DeviceBox.SelectedItem as Choice)?.Label??""))Select(DeviceBox,"auto");
            Select(EncoderBox,preferences.Encoder);ready=true;updating=false;
            UpdateHardwareText();
            Log(T("실행 가능한 장치와 인코더를 확인했습니다.","Available devices and encoders checked."));
            if(useTray&&preferences.CheckUpdates)_=CheckUpdatesAsync();
        }catch(Exception ex){Log(ex.Message);HardwareText.Text=ex.Message;updating=false;}
        UpdateControls();
        ScheduleEstimate();
    }
    private void Translate(){
        bool old=updating;updating=true;
        TranslateCapture();
        Tagline.Text=T("30fps 게임 녹화를 60fps로 변환","Convert 30fps game recordings to 60fps");AddButton.Content=T("영상 추가","Add videos");QueueTitle.Text=T("입력 영상","Input videos");
        Subtitle.Text=T("반복된 중복 프레임을 찾아내고, 그 자리를 RIFE AI 보간 프레임으로 채웁니다.","Finds repeated duplicate frames and fills them with RIFE AI-interpolated frames.");
        DropText.Text=T("영상을 끌어 놓으세요","Drop your videos here");DropHint.Text=T("또는 클릭하여 파일을 선택하세요","Or click here to choose files");
        FileMetricLabel.Text=T("파일 프레임레이트","File frame rate");MotionMetricLabel.Text=T("추정 움직임","Estimated motion");Details.Header=T("처리 기록","Activity");
        EstimateTitle.Text=T("예상 소요 시간","Estimated time");EstimateAgain.ToolTip=T("현재 설정으로 다시 계산","Recalculate for current settings");
        SettingsTitle.Text=T("출력 설정","Output settings");OutputLabel.Text=T("저장 폴더","Output folder");PresetLabel.Text=T("프리셋","Preset");
        FpsLabel.Text=T("목표 프레임레이트","Output frame rate");DeviceLabel.Text=T("추론 장치","Inference device");EncoderLabel.Text=T("인코더","Encoder");
        HudLabel.Text=T("고정 HUD 영역 (X,Y,W,H; 선택)","Static HUD region (X,Y,W,H; optional)");ModelLabel.Text=T("RIFE 모델","RIFE model");
        // Facts from notes/research.md R1, R2 and R7 (official README, same architecture, measured PSNR/speed).
        ModelHelp.ToolTip=Hangul.KeepWords(T("RIFE 4.25 (기본값): 공식 프로젝트가 대부분의 장면에 권장하는 모델입니다.\nRIFE 4.26: 가장 최근에 공개된 모델로, 구조는 같고 새로 학습했습니다.\n두 모델은 속도가 같고, 게임 녹화 시험에서 화질 차이가 거의 없었습니다. 어색한 장면이 있으면 다른 모델로 바꿔 보세요.",
            "RIFE 4.25 (default): recommended by the official project for most scenes.\nRIFE 4.26: the latest release, same architecture with newly trained weights.\nBoth run at the same speed and looked almost identical in our game-recording tests. If a scene looks wrong, try the other one."));
        System.Windows.Automation.AutomationProperties.SetName(ModelHelp,T("RIFE 모델 설명","About RIFE models"));
        HardwareCheck.Content=T("하드웨어 인코딩 사용","Use hardware encoding");VerifyCheck.Content=T("변환 후 구간 검증","Verify output samples");VerifyCheck.ToolTip=T("처음·중간·끝 각 1분만 검사합니다(최대 3분, 겹치면 병합). 전체 메타데이터도 확인하며 검사 밖의 오류는 놓칠 수 있습니다.","Checks one minute each at the start, middle and end (up to three minutes; overlaps merged), plus global metadata. Errors outside the samples may be missed.");
        RemoveButton.Content=T("선택 제거","Remove");StartButton.Content=T("일괄 변환","Convert all");SelectedButton.Content=T("선택 변환","Convert selected");CancelButton.Content=T("취소","Cancel");
        OpenButton.Content=T("결과 열기","Open result");FolderButton.Content=T("폴더 열기","Open folder");UpdatesButton.ToolTip=T("업데이트","Updates");SettingsButton.ToolTip=T("설정","Settings");
        MinimizeButton.ToolTip=T("최소화","Minimize");MaximizeButton.ToolTip=T("최대화 / 복원","Maximize / restore");CloseButton.ToolTip=T("닫기","Close");BrowseButton.ToolTip=T("저장 폴더 선택","Choose output folder");
        foreach(var button in new[]{MinimizeButton,MaximizeButton,CloseButton,BrowseButton,UpdatesButton,SettingsButton})System.Windows.Automation.AutomationProperties.SetName(button,button.ToolTip.ToString());
        System.Windows.Automation.AutomationProperties.SetName(EmptyPanel,T("영상 파일 선택","Choose video files"));
        OutputBox.ToolTip=T("비워 두면 원본 영상과 같은 폴더에 저장합니다.","Leave empty to save beside the source video.");
        OutputHint.Text=T("원본 영상과 같은 폴더","Same folder as the source");
        RefreshShortcutHints();
        foreach(var (control,name) in new (UIElement,string)[]{(OutputBox,OutputLabel.Text),(FpsBox,FpsLabel.Text),(PresetBox,PresetLabel.Text),(DeviceBox,DeviceLabel.Text),
            (EncoderBox,EncoderLabel.Text),(ModelBox,ModelLabel.Text),(HudBox,HudLabel.Text)})
            System.Windows.Automation.AutomationProperties.SetName(control,name);
        RefreshPresets(Selected(PresetBox));
        Fill(FpsBox,[new("auto",T("입력에 맞춤 (권장)","Match input (recommended)")),new("60","60fps"),new("60000/1001","59.94fps"),new("120","120fps"),new("120000/1001","119.88fps")]);
        RefreshOptions();foreach(var entry in Entries)entry.Refresh();UpdateMetrics();updating=old;UpdateControls();
        UpdateHardwareText();
        if(tray?.ContextMenuStrip is BrandTrayMenu menu)menu.Translate(English);
    }
    private void UpdateHardwareText(){
        if(inventory is null){HardwareText.Text=T("하드웨어를 확인하고 있습니다…","Checking hardware…");return;}
        HardwareText.Text=$"{inventory.CpuModel??T("CPU 미확인","CPU unknown")} · {inventory.LogicalProcessors} {T("논리 프로세서","logical processors")}\n"+
            (inventory.Gpus.Count==0?T("Vulkan GPU 없음 · CPU 추론 사용 (느림)","No Vulkan GPU · CPU inference (slow)"):
                string.Join(" · ",inventory.Gpus.Select(x=>$"{x.Name} ({(x.Type==0?T("전용","discrete"):x.Type==1?T("내장","integrated"):x.Kind)}, "+
                    (x.DedicatedBytes is { } b?$"{b/(1024d*1024*1024):0.#}GB":T("공유 메모리","shared memory"))+
                    (x.BudgetBytes is { } available?$", {T("사용 가능","available")} {available/(1024d*1024*1024):0.#}GB":"")+
                    (x.InferencePassed==false?T(", 추론 불가",", inference unavailable"):"")+")")));
    }
    private string UserMessage(string message,string stage){
        if(!English)return message;
        if(stage=="fallback")return message.Contains("ncnn",StringComparison.Ordinal)?"GPU add-on failed; retrying with ncnn Vulkan.":"Inference worker changed; CPU fallback can be much slower.";
        if(stage=="encoder_fallback")return "Retrying with software encoding; performance can be lower.";
        if(stage=="encoder_tuning_fallback")return "Quality acceleration is unavailable for this job; retrying the base encoder settings.";
        if(stage=="hardware")return "Inspecting device/encoder capabilities and transfer cost.";
        if(stage=="analyze"&&message.StartsWith("HDR(",StringComparison.Ordinal))return "HDR video is tone-mapped to SDR (BT.709, 8-bit) for conversion.";
        return message.Replace("원본 비트레이트 정보가 없어 품질 기준으로 인코딩합니다.","Source bitrate unavailable; encoding by quality instead.").Replace("FP16 저장·FP32 연산","FP16 storage / FP32 arithmetic").Replace("FP32 저장·연산","FP32 storage / arithmetic")
            .Replace("고비트 심도","high bit depth").Replace("소프트웨어 인코더:","Software encoder:").Replace("인코더:","Encoder:")
            .Replace("하드웨어 인코딩보다 느릴 수 있습니다.","Can be slower than hardware encoding.")
            .Replace("CPU 추론: Vulkan보다 처리 속도가 크게 느려질 수 있습니다.","CPU inference can be much slower than Vulkan.")
            .Replace("사용 가능한 Vulkan GPU가 없어 CPU로 대체합니다. 처리 속도가 크게 느려질 수 있습니다.","No usable Vulkan GPU. CPU fallback can be much slower.");
    }
    private void RefreshOptions(){
        bool old=updating;updating=true;
        bool deep=Entries.Any(x=>x.Analysis?.Source.BitDepth>8),hw=HardwareCheck.IsChecked==true;
        Fill(DeviceBox,new[]{new Choice("auto",T("자동 선택","Automatic"))}.Concat((inventory?.Gpus??new()).Where(x=>x.InferencePassed!=false&&x.SupportsWarp).Select(x=>new Choice($"gpu:{x.Index}",x.Name)))
            .Append(new("cpu",T("CPU (느림)","CPU (slow)"))));
        // The codec comes from the export preset; the encoder list offers what can encode it on this PC.
        var preset=CurrentPreset;
        PresetBox.ToolTip=OutputPresets.Describe(preset,English);
        string? codec=preset.Codec is "auto" or "source"?null:preset.Codec;
        if(preset.Codec=="source"&&(QueueList.SelectedItem as QueueEntry)?.Analysis?.Source is {} source)codec=OutputPresets.SourceCodec(source,encoders,hw);
        try{codec??=EncoderPolicy.DefaultCodec(encoders,preset.Platform!="none",deep,hw);}catch(InvalidOperationException){}
        Fill(EncoderBox,new[]{new Choice("auto",T("자동 선택","Automatic"))}.Concat(encoders.Where(x=>x.Available&&x.Codec==codec&&(!deep||x.TenBit)&&(hw||!x.Hardware))
            .Select(x=>new Choice(x.Name,EncoderName(x)))));
        updating=old;
    }
    // Plain labels for non-specialists: which chip encodes, not the FFmpeg encoder name.
    // Each codec has at most one encoder per backend, so the labels stay unique.
    internal static string EncoderName(EncoderCapability encoder)=>encoder.Backend switch{
        "nvenc"=>"NVENC (GPU)","amf"=>"AMF (GPU)","qsv"=>"QSV (GPU)",_=>encoder.Hardware?encoder.Name+" (GPU)":"CPU"};
    private void LanguageClicked(object sender,RoutedEventArgs e){if(busy||sender is not Button{Tag:string value}||value==language)return;SetLanguage(value,true);if(!updating&&IsInitialized)Translate();}
    // A two-segment switch: the accent thumb slides under the selected language.
    private void SetLanguage(string? value,bool animate){
        language=value=="en"?"en":"ko";
        KoreanButton.Foreground=new SolidColorBrush(Color.FromRgb(language=="ko"?(byte)0x14:(byte)0xA6,language=="ko"?(byte)0x18:(byte)0xB1,language=="ko"?(byte)0x1B:(byte)0xB8));
        EnglishButton.Foreground=new SolidColorBrush(Color.FromRgb(language=="en"?(byte)0x14:(byte)0xA6,language=="en"?(byte)0x18:(byte)0xB1,language=="en"?(byte)0x1B:(byte)0xB8));
        MoveLanguageThumb(animate);
    }
    private void MoveLanguageThumb(bool animate){
        double target=English?LanguageThumb.ActualWidth:0;
        if(!animate||!IsLoaded){LanguageThumbShift.BeginAnimation(TranslateTransform.XProperty,null);LanguageThumbShift.X=target;return;}
        LanguageThumbShift.BeginAnimation(TranslateTransform.XProperty,new System.Windows.Media.Animation.DoubleAnimation(target,TimeSpan.FromMilliseconds(240))
            {EasingFunction=new System.Windows.Media.Animation.CubicEase{EasingMode=System.Windows.Media.Animation.EasingMode.EaseOut}});
    }
    private void OptionsChanged(object sender,RoutedEventArgs e){if(!updating&&IsInitialized){RefreshOptions();ScheduleEstimate();}}
    private async void AddClicked(object sender,RoutedEventArgs e){
        if(busy)return;
        if(FilePicker is not null){if(FilePicker() is {Length:>0} files)await AddFilesAsync(files);return;}
        var dialog=new OpenFileDialog{Multiselect=true,Title=T("영상 선택","Choose videos"),Filter="Video|*.mp4;*.mkv;*.mov;*.ts|All files|*.*"};
        if(dialog.ShowDialog(this)==true)await AddFilesAsync(dialog.FileNames);
    }
    public async Task AddFilesAsync(IEnumerable<string> files){
        if(busy)return;busy=true;work=new CancellationTokenSource();StatusText.Text=T("영상 분석 중","Analyzing video");UpdateControls();
        try{foreach(string file in files){
            if(work.IsCancellationRequested)break;
            if(!File.Exists(file)||Entries.Any(x=>string.Equals(x.Input,file,StringComparison.OrdinalIgnoreCase)))continue;
            var entry=new QueueEntry(Path.GetFullPath(file),T){State="analyze"};Entries.Add(entry);QueueList.SelectedItem=entry;UpdateControls();
            try{entry.Analysis=await Task.Run(()=>pipeline.AnalyzeAsync(entry.Input,cancel:work.Token));entry.State="ready";
                if(entry.Analysis.Source.HdrInput is { } hdr)Log(entry.Name+": "+T(ConversionPipeline.ToneMapNotice(hdr),$"HDR ({hdr.Name}) video is tone-mapped to SDR (BT.709, 8-bit) for conversion."));
                _=LoadThumbnailAsync(entry);}
            catch(OperationCanceledException){entry.State="cancelled";}catch(Exception ex){entry.State="error";Log(entry.Name+": "+ex.Message);}
            entry.Refresh();
        }}finally{busy=false;work.Dispose();work=null;StatusText.Text=T("변환 준비","Ready");RefreshOptions();UpdateControls();ScheduleEstimate();if(closing)Close();}
    }
    // Loaded after analysis without holding up the queue; a failure simply keeps the placeholder icon.
    private async Task LoadThumbnailAsync(QueueEntry entry){
        if(entry.Analysis is not { } analysis)return;
        try{
            byte[] png=await Task.Run(()=>Thumbnail.CaptureAsync(paths,analysis.Source,256,lifetime.Token));
            var image=new System.Windows.Media.Imaging.BitmapImage();
            image.BeginInit();image.CacheOption=System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;image.StreamSource=new MemoryStream(png);image.EndInit();image.Freeze();
            entry.Thumbnail=image;entry.Refresh();
        }catch(Exception ex)when(ex is OperationCanceledException or IOException or TimeoutException or NotSupportedException or InvalidOperationException){}
    }
    // Batch: every remaining entry in list order. Selection: only the given entries, still in list order.
    public async Task RunQueueAsync(IReadOnlyCollection<QueueEntry>? only=null){
        if(busy||!ready)return;
        busy=true;work=new CancellationTokenSource();UpdateControls();
        try{
            if(estimateTask is not null)await estimateTask;work.Token.ThrowIfCancellationRequested();
            var options=CurrentOptions();
            foreach(var entry in Entries.Where(x=>x.Output is null&&x.Analysis is not null&&(only is null||only.Contains(x))).ToArray()){
                work.Token.ThrowIfCancellationRequested();QueueList.SelectedItem=entry;entry.State="convert";entry.Refresh();
                string folder=string.IsNullOrWhiteSpace(OutputBox.Text)?Path.GetDirectoryName(entry.Input)!:Path.GetFullPath(OutputBox.Text);
                string stem=Path.GetFileNameWithoutExtension(entry.Input)+"_smooth";string output=Path.Combine(folder,stem+".mp4");int index=2;
                while(File.Exists(output))output=Path.Combine(folder,stem+"_"+index+++".mp4");
                var progress=new Progress<ConversionProgress>(p=>{
                    ProgressBar.Value=p.Fraction;StatusText.Text=Stage(p.Stage);
                    RateText.Text=p.Frames>0?$"{p.Frames:N0} / {p.Total:N0} · {T("처리","Processing")} {p.Frames/Math.Max(.01,p.Seconds):0.#}fps"+
                        (p.Stage=="convert"&&p.Remaining is { } remaining?$" · {T("남음","Remaining")} {TimeEstimates.Duration(remaining,English)}":""):"";
                    if(!string.IsNullOrEmpty(p.Message))Log(UserMessage(p.Message,p.Stage));
                });
                try{
                    var result=await Task.Run(()=>pipeline.RunAsync(entry.Input,output,options,progress,work.Token));
                    entry.Result=result;entry.Output=result.Output;entry.State=result.Verification is null?"unverified":result.Verification.Ok?"done":"warning";
                    Log($"{Path.GetFileName(result.Output)} · {result.Fps}fps · {result.Device} · {result.Encoder} · {result.Seconds:0.0}s");
                }catch(OperationCanceledException){entry.State="cancelled";entry.Refresh();throw;}
                catch(Exception ex){entry.State="error";Log(ex.Message);}
                entry.Refresh();UpdateControls();
            }
        }catch(OperationCanceledException){StatusText.Text=T("취소됨 · 미완성 파일을 정리했습니다.","Cancelled · incomplete output removed.");}
        catch(Exception ex){Log(ex.Message);StatusText.Text=ex.Message;}
        finally{busy=false;work?.Dispose();work=null;Save();UpdateControls();if(closing)Close();}
    }
    private HudRegion[] ParseHud()=>string.IsNullOrWhiteSpace(HudBox.Text)?Array.Empty<HudRegion>():HudBox.Text.Split(';',StringSplitOptions.RemoveEmptyEntries).Select(x=>{
        int[] p=x.Split(',').Select(int.Parse).ToArray();if(p.Length!=4)throw new ArgumentException("HUD: X,Y,W,H");return new HudRegion(p[0],p[1],p[2],p[3]);}).ToArray();
    private string Stage(string stage)=>stage switch{"analyze"=>T("영상 분석 중","Analyzing video"),"hardware"=>T("장치 확인 중","Checking hardware"),"model"=>T("모델 준비 중","Preparing model"),
        "convert"=>T("보간·인코딩 중","Interpolating and encoding"),"mux"=>T("마무리 중","Finalizing"),"verify"=>T("처음·중간·끝 구간 검증 중","Verifying start, middle and end"),
        "done"=>T("변환 완료","Conversion complete"),"warning"=>T("검증 경고 · 결과를 확인하세요","Quality warning · review the output"),"fallback"=>T("CPU 대체 경로 사용","Using CPU fallback"),"encoder_fallback"=>T("소프트웨어 인코딩으로 대체","Software encoding fallback"),_=>stage};
    private void UpdateControls(){
        if(!IsInitialized)return;
        EmptyPanel.Visibility=Entries.Count==0?Visibility.Visible:Visibility.Collapsed;AddButton.IsEnabled=!busy&&!measuring;RemoveButton.IsEnabled=!busy&&QueueList.SelectedItems.Count>0;
        EmptyPanel.IsEnabled=!busy;QueueCount.Text=Entries.Count.ToString();UpdateMetrics();
        SettingsPanel.IsEnabled=!busy;LanguageSwitch.IsEnabled=!busy;CancelButton.IsEnabled=work is not null||measuring;
        StartButton.IsEnabled=ready&&!busy&&!measuring&&Entries.Any(x=>x.Analysis is not null&&x.Output is null);
        SelectedButton.IsEnabled=ready&&!busy&&!measuring&&SelectedForConversion().Length>0;
        OpenButton.IsEnabled=(QueueList.SelectedItem as QueueEntry)?.Output is { } output&&File.Exists(output);UpdatesButton.IsEnabled=!busy;SettingsButton.IsEnabled=!busy;
        if(!busy&&Entries.Count==0)StatusText.Text=T("영상을 추가해 주세요.","Add a video to begin.");
        RefreshEstimateText();
    }
    internal void Log(string text){LogBox.AppendText(text+Environment.NewLine);LogBox.ScrollToEnd();}
    internal string LogText=>LogBox.Text;
    private async void StartClicked(object sender,RoutedEventArgs e)=>await RunQueueAsync();
    private async void SelectedClicked(object sender,RoutedEventArgs e)=>await RunQueueAsync(SelectedForConversion());
    internal QueueEntry[] SelectedInListOrder()=>Entries.Where(x=>QueueList.SelectedItems.Contains(x)).ToArray();
    private QueueEntry[] SelectedForConversion()=>SelectedInListOrder().Where(x=>x.Analysis is not null&&x.Output is null).ToArray();
    private void Renumber(){for(int i=0;i<Entries.Count;i++)if(Entries[i].Number!=i+1){Entries[i].Number=i+1;Entries[i].Refresh();}}
    // Reordering: dragging an item moves it live under the pointer; locked while a job runs.
    private const string QueueFormat="FrameDoubler.QueueEntry";
    private Point dragStart;private QueueEntry? dragCandidate;private DateTime lastDragScroll;
    private void QueuePressed(object sender,MouseButtonEventArgs e){dragStart=e.GetPosition(QueueList);dragCandidate=ItemAt(e.OriginalSource)?.DataContext as QueueEntry;}
    private void QueueDragMove(object sender,MouseEventArgs e){
        if(dragCandidate is null||e.LeftButton!=MouseButtonState.Pressed||busy||Entries.Count<2)return;
        var moved=e.GetPosition(QueueList)-dragStart;
        if(Math.Abs(moved.X)<SystemParameters.MinimumHorizontalDragDistance&&Math.Abs(moved.Y)<SystemParameters.MinimumVerticalDragDistance)return;
        var entry=dragCandidate;dragCandidate=null;
        DragDrop.DoDragDrop(QueueList,new DataObject(QueueFormat,entry),DragDropEffects.Move);
    }
    private void QueueDragOver(object sender,DragEventArgs e){
        if(!e.Data.GetDataPresent(QueueFormat))return;   // files are handled by the window
        e.Effects=busy?DragDropEffects.None:DragDropEffects.Move;e.Handled=true;
        if(busy||e.Data.GetData(QueueFormat) is not QueueEntry moving)return;
        // Near the top or bottom edge the list scrolls (one item per 0.25 s, the list scrolls by item)
        // so entries hidden above or below can be reached.
        double y=e.GetPosition(QueueList).Y;
        if(Scroller(QueueList) is { } scroll&&DateTime.UtcNow-lastDragScroll>TimeSpan.FromMilliseconds(250)){
            if(y<48){scroll.LineUp();lastDragScroll=DateTime.UtcNow;}else if(y>QueueList.ActualHeight-48){scroll.LineDown();lastDragScroll=DateTime.UtcNow;}
        }
        int from=Entries.IndexOf(moving),insert=InsertIndex(e);
        int to=insert>from?insert-1:insert;
        if(from>=0&&to!=from)MoveEntry(moving,to);
    }
    // Insertion point = before the first item whose vertical middle is below the pointer. Comparing against
    // item middles (not the element under the pointer) keeps it stable across the gaps between cards.
    private int InsertIndex(DragEventArgs e){
        for(int i=0;i<Entries.Count;i++)
            if(QueueList.ItemContainerGenerator.ContainerFromIndex(i) is ListBoxItem item&&e.GetPosition(item).Y<item.ActualHeight/2)return i;
        return Entries.Count;
    }
    private static ScrollViewer? Scroller(DependencyObject root){
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var child=VisualTreeHelper.GetChild(root,i);if(child is ScrollViewer found)return found;if(Scroller(child) is { } deeper)return deeper;}
        return null;
    }
    private void QueueDropped(object sender,DragEventArgs e){if(e.Data.GetDataPresent(QueueFormat))e.Handled=true;}
    internal void MoveEntry(QueueEntry entry,int index){int from=Entries.IndexOf(entry);if(busy||from<0||index<0||index>=Entries.Count||from==index)return;Entries.Move(from,index);}
    private static ListBoxItem? ItemAt(object source){
        var node=source as DependencyObject;
        while(node is not null and not ListBoxItem)node=node is Visual?VisualTreeHelper.GetParent(node):LogicalTreeHelper.GetParent(node);
        return node as ListBoxItem;
    }
    private void CancelClicked(object sender,RoutedEventArgs e){work?.Cancel();estimateCancel?.Cancel();}
    private void RemoveClicked(object sender,RoutedEventArgs e)=>RemoveSelected(false);
    // From the keyboard the next video is selected, so Delete can be pressed again.
    private void RemoveSelected(bool selectNext){
        var selected=SelectedInListOrder();
        if(!busy&&selected.Length>0){
            int first=Entries.IndexOf(selected[0]);
            foreach(var item in selected)Entries.Remove(item);
            if(selectNext&&Entries.Count>0){QueueList.SelectedItem=Entries[Math.Min(first,Entries.Count-1)];FocusSelected();}
        }
        RefreshOptions();UpdateControls();
    }
    // Moves the selected videos one place as a block; the first or last one stops at the list edge.
    private void MoveSelected(int direction){
        var selected=SelectedInListOrder().ToHashSet();
        if(busy||selected.Count==0)return;
        if(direction<0){
            for(int i=0,limit=0;i<Entries.Count;i++){if(!selected.Contains(Entries[i]))continue;if(i>limit){Entries.Move(i,i-1);limit=i;}else limit=i+1;}
        }else{
            for(int i=Entries.Count-1,limit=Entries.Count-1;i>=0;i--){if(!selected.Contains(Entries[i]))continue;if(i<limit){Entries.Move(i,i+1);limit=i;}else limit=i-1;}
        }
        FocusSelected();
    }
    private void FocusSelected(){
        if(QueueList.SelectedItem is not QueueEntry entry)return;
        QueueList.ScrollIntoView(entry);QueueList.UpdateLayout();
        (QueueList.ItemContainerGenerator.ContainerFromItem(entry) as ListBoxItem)?.Focus();
    }

    // ---- file conversion shortcuts (Settings → Shortcuts), only on this page and only in this window
    private void ConvertKeyDown(object sender,KeyEventArgs e){
        if(e.Handled||page!="convert")return;
        var key=e.Key==Key.System?e.SystemKey:e.Key;
        // Holding a move key keeps moving; other actions run once per press.
        if(e.IsRepeat&&!Shortcut.Matches(preferences.ShortcutMoveUp,Keyboard.Modifiers,key)&&!Shortcut.Matches(preferences.ShortcutMoveDown,Keyboard.Modifiers,key))return;
        if(HandleConvertShortcut(key,Keyboard.Modifiers))e.Handled=true;
    }
    // Keys the focused text box or drop-down list uses itself: typing, Delete, Enter, arrows, the
    // editing chords (Ctrl+A/C/V/X/Z/Y, Ctrl+arrows) and Alt+Up/Down on a drop-down. Other chords
    // such as Ctrl+O still reach the shortcuts.
    private static bool OwnedByFocusedControl(object? focus,Key key,ModifierKeys modifiers){
        bool chord=(modifiers&(ModifierKeys.Control|ModifierKeys.Alt))!=0;
        if(focus is System.Windows.Controls.Primitives.TextBoxBase or PasswordBox)
            return !chord||modifiers.HasFlag(ModifierKeys.Control)&&key is Key.A or Key.C or Key.V or Key.X or Key.Z or Key.Y or Key.Insert
                or Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.Delete or Key.Back;
        if(focus is ComboBox or ComboBoxItem)return !chord||modifiers==ModifierKeys.Alt&&key is Key.Up or Key.Down;
        return false;
    }
    internal bool HandleConvertShortcut(Key key,ModifierKeys modifiers){
        if(OwnedByFocusedControl(Keyboard.FocusedElement,key,modifiers))return false;
        // A bare Enter or Space presses the focused button; it acts on the list only from inside it.
        if(modifiers==ModifierKeys.None&&key is Key.Enter or Key.Space&&!QueueList.IsKeyboardFocusWithin)return false;
        var p=preferences;
        bool Is(string shortcut)=>Shortcut.Matches(shortcut,modifiers,key);
        if(Is(p.ShortcutAddVideos)){if(AddButton.IsEnabled)AddClicked(this,new RoutedEventArgs());return true;}
        if(Is(p.ShortcutRemove)){RemoveSelected(true);return true;}
        if(Is(p.ShortcutSelectAll)){if(Entries.Count>0){QueueList.SelectAll();FocusSelected();}return true;}
        if(Is(p.ShortcutMoveUp)){MoveSelected(-1);return true;}
        if(Is(p.ShortcutMoveDown)){MoveSelected(1);return true;}
        if(Is(p.ShortcutConvertAll)){if(StartButton.IsEnabled)StartClicked(this,new RoutedEventArgs());return true;}
        if(Is(p.ShortcutConvertSelected)){if(SelectedButton.IsEnabled)SelectedClicked(this,new RoutedEventArgs());return true;}
        if(Is(p.ShortcutOpenResult)){if(OpenButton.IsEnabled)OpenClicked(this,new RoutedEventArgs());return true;}
        return false;
    }
    // Button tooltips name the current key, so a changed shortcut is visible where it is used.
    internal void RefreshShortcutHints(){
        string Hint(string text,string shortcut)=>Shortcut.Normalize(shortcut) is {Length:>0} key?text+Environment.NewLine+T("단축키 ","Shortcut ")+key:text;
        var p=preferences;
        AddButton.ToolTip=Hint(T("영상 파일을 고릅니다. 끌어 놓아도 됩니다.","Choose video files. You can also drop them here."),p.ShortcutAddVideos);
        RemoveButton.ToolTip=Hint(T("선택한 영상을 목록에서 뺍니다.","Removes the selected videos from the list."),p.ShortcutRemove);
        StartButton.ToolTip=Hint(T("변환하지 않은 영상을 목록 번호순으로 모두 변환합니다.","Converts every remaining video in list order."),p.ShortcutConvertAll);
        SelectedButton.ToolTip=Hint(T("목록에서 선택한 영상만 번호순으로 변환합니다. Ctrl·Shift로 여러 개를 고를 수 있습니다.","Converts only the selected videos in list order. Use Ctrl or Shift to select several."),p.ShortcutConvertSelected);
        OpenButton.ToolTip=Hint(T("선택한 영상의 변환 결과를 엽니다. 목록에서 변환이 끝난 영상을 두 번 클릭해도 됩니다.","Opens the converted file of the selected video. You can also double-click a converted video in the list."),p.ShortcutOpenResult);
    }
    private void QueueChanged(object sender,SelectionChangedEventArgs e){UpdateMetrics();UpdateControls();}
    private void UpdateMetrics(){
        var analysis=(QueueList.SelectedItem as QueueEntry)?.Analysis;
        FileMetric.Text=analysis is null?"—":analysis.Source.Fps.ToDouble().ToString("0.##");
        MotionMetric.Text=analysis?.MotionFps is { } fps?fps.ToString("0.##"):"—";
    }
    private void ChooseFolder(object sender,RoutedEventArgs e){var dialog=new OpenFolderDialog();if(dialog.ShowDialog(this)==true)OutputBox.Text=dialog.FolderName;}
    // Diagnostics replace the shell so a check never starts a player or Explorer.
    internal Action<string>? Opener{get;set;}
    private void Open(string path){if(Opener is not null)Opener(path);else Process.Start(new ProcessStartInfo(path){UseShellExecute=true});}
    // Double-clicking a converted video plays the result in the default player. Videos that are not
    // converted yet (or whose result was moved away) do nothing; the scroll bar is not an item.
    private void QueueDoubleClicked(object sender,MouseButtonEventArgs e){
        if(e.ChangedButton!=MouseButton.Left||ItemAt(e.OriginalSource)?.DataContext is not QueueEntry{Output:{} output}||!File.Exists(output))return;
        Open(output);e.Handled=true;
    }
    private void OpenClicked(object sender,RoutedEventArgs e){if(QueueList.SelectedItem is QueueEntry{Output:not null} item)Open(item.Output);}
    private void OpenFolderClicked(object sender,RoutedEventArgs e){string? folder=(QueueList.SelectedItem as QueueEntry)?.Output is { } output?Path.GetDirectoryName(output):OutputBox.Text;if(!string.IsNullOrEmpty(folder)&&Directory.Exists(folder))Open(folder);}
    private async void UpdatesClicked(object sender,RoutedEventArgs e)=>await ShowUpdatesAsync();
    private void SettingsClicked(object sender,RoutedEventArgs e){if(!busy)OpenSettings(null);}
    // Export presets: the four built-ins; the selection is saved in settings.ini.
    internal OutputPreset CurrentPreset=>OutputPresets.Find(Selected(PresetBox));
    private void RefreshPresets(string? select){
        bool old=updating;updating=true;
        Fill(PresetBox,OutputPresets.BuiltIns.Select(x=>new Choice(x.Id,OutputPresets.DisplayName(x,English))),select);
        updating=old;
    }
    // Used by the settings window; changes apply immediately and are saved to settings.ini.
    internal Preferences Settings=>preferences;
    internal bool Working=>busy||measuring;
    internal bool CanManageAcceleration=>!busy&&!measuring;
    internal void RefreshAcceleration()=>ScheduleEstimate(true);
    internal void ChangeSettings(Func<Preferences,Preferences> change){string accelerator=preferences.Accelerator;preferences=change(preferences);Save();if(accelerator!=preferences.Accelerator)ScheduleEstimate(true);}
    // Settings → Advanced shows the main window's own advanced controls, so device/encoder lists,
    // estimates and saving keep working unchanged. They return to the hidden host when the page closes.
    internal FrameworkElement BorrowAdvanced(){Detach(AdvancedPanel);return AdvancedPanel;}
    internal void ReturnAdvanced(){if(ReferenceEquals(AdvancedHost.Content,AdvancedPanel))return;Detach(AdvancedPanel);AdvancedHost.Content=AdvancedPanel;Save();}
    private static void Detach(FrameworkElement element){
        switch(element.Parent){case ContentControl host:host.Content=null;break;case Decorator border:border.Child=null;break;case Panel panel:panel.Children.Remove(element);break;}
    }
    private async Task CheckUpdatesAsync(){try{availableRelease=await Updates.CheckAsync(lifetime.Token);MarkUpdate();}
        catch(OperationCanceledException){}catch(Exception ex){Log(T("업데이트 확인: ","Update check: ")+ex.Message);}}
    private void MarkUpdate(){if(availableRelease is null)return;UpdatesButton.ToolTip=T("업데이트 있음","Update available");UpdatesButton.Foreground=(Brush)new BrushConverter().ConvertFromString("#FF8C60")!;}
    private bool checkingUpdates;
    // No update: a compact dialog that just says so. Only a real update opens the full window
    // with the release notes and the download → verify → preserve → restart steps.
    private async Task ShowUpdatesAsync(){
        if(busy||checkingUpdates)return;
        checkingUpdates=true;
        try{
            var release=availableRelease;
            if(release is null){
                var dialog=new BrandDialog(this,T("업데이트","Updates"));
                var ok=new DialogButton(T("확인","OK"),DialogChoice.Cancel,"PrimaryButton",true);
                dialog.Configure(BrandDialog.Glyphs.Update,T("업데이트를 확인하고 있습니다","Checking for updates"),T("GitHub에서 최신 안정 릴리스를 확인합니다.","Looking up the latest stable release on GitHub."),null,
                    new DialogButton(T("취소","Cancel"),DialogChoice.Cancel,"TextButton"));
                dialog.Loaded+=async(_,_)=>{
                    try{release=await Updates.CheckAsync(dialog.ClosedToken);}
                    catch(OperationCanceledException){return;}
                    catch(Exception ex){if(dialog.IsVisible)dialog.Configure(BrandDialog.Glyphs.Error,T("업데이트를 확인하지 못했습니다","Couldn't check for updates"),ex.Message,null,ok);return;}
                    if(!dialog.IsVisible)return;
                    if(release is not null){dialog.Close();return;}
                    dialog.Configure(BrandDialog.Glyphs.Done,T("최신 버전입니다","You're up to date"),
                        T($"지금 사용 중인 {Updates.Current} 버전이 최신입니다.",$"FrameDoubler {Updates.Current} is the latest version."),null,ok);
                };
                dialog.ShowDialog();
                if(release is null)return;
                availableRelease=release;MarkUpdate();
            }
            new UpdateWindow(this,release).ShowDialog();
        }finally{checkingUpdates=false;}
    }
    private async void OnDrop(object sender,DragEventArgs e){DropBorder.BorderBrush=(Brush)new BrushConverter().ConvertFromString("#3A454C")!;if(e.Data.GetData(DataFormats.FileDrop) is string[] files){ShowPage("convert");await AddFilesAsync(files);}}
    private void OnDragOver(object sender,DragEventArgs e){e.Effects=!busy&&e.Data.GetDataPresent(DataFormats.FileDrop)?DragDropEffects.Copy:DragDropEffects.None;DropBorder.BorderBrush=(Brush)new BrushConverter().ConvertFromString(e.Effects==DragDropEffects.Copy?"#FF8C60":"#3A454C")!;e.Handled=true;}
    private void SetupTray(){
        if(tray is not null)return;
        using var stream=System.Windows.Application.GetResourceStream(new Uri("Assets/app.ico",UriKind.Relative))!.Stream;
        using var icon=new System.Drawing.Icon(stream);
        tray=new Forms.NotifyIcon{Icon=(System.Drawing.Icon)icon.Clone(),Text="FrameDoubler",Visible=true,ContextMenuStrip=CreateTrayMenu()};
        tray.DoubleClick+=(_,_)=>Restore();
    }
    internal bool ActivateExistingWindow(){if(exitConfirmed||closing)return false;Restore();return true;}
    private void Restore(){
        tray?.ContextMenuStrip?.Close();
        bool minimized=WindowState==WindowState.Minimized;var previous=restoreState;
        Show();if(minimized)WindowState=previous;
        var target=OwnedWindows.Cast<Window>().LastOrDefault(x=>x.IsVisible)??this;
        if(target.WindowState==WindowState.Minimized)target.WindowState=WindowState.Normal;
        target.Activate();SingleInstance.Foreground(target);
    }
    private Preferences CurrentPreferences(){
        preferences=preferences with{Language=language,OutputDirectory=OutputBox.Text,Preset=Selected(PresetBox),Fps=Selected(FpsBox),
            Device=Selected(DeviceBox),DeviceName=(DeviceBox.SelectedItem as Choice)?.Label??"",Encoder=Selected(EncoderBox),Model=Selected(ModelBox),
            Verify=VerifyCheck.IsChecked==true,HardwareEncoding=HardwareCheck.IsChecked==true,Accelerator=preferences.Accelerator,
            CheckUpdates=preferences.CheckUpdates,CloseAction=preferences.CloseAction,MinimizeAction=preferences.MinimizeAction};
        return preferences;
    }
    private void Save(){try{CurrentPreferences().Save();}catch(Exception ex){Log(ex.Message);}}
    private void OnClosing(object? sender,CancelEventArgs e){
        // Alt+F4 and the taskbar's "Close window" also go through the confirmation.
        if(!exitConfirmed){e.Cancel=true;Dispatcher.BeginInvoke(()=>RequestExit(ExitTrigger.Close));return;}
        // Running jobs are cancelled first; their FFmpeg/engine processes are killed and incomplete output removed,
        // then the queue/estimate completion calls Close() again. The job object is the last safety net.
        if(busy||measuring){e.Cancel=true;closing=true;StatusText.Text=T("작업을 취소하고 정리한 뒤 종료합니다…","Cancelling and cleaning up before exit…");work?.Cancel();estimateCancel?.Cancel();return;}
        capture?.Shutdown();
        lifetime.Cancel();Save();DisposeTray();
    }
    public void PreviewLanguage(string value){SetLanguage(value,false);Translate();}
}
