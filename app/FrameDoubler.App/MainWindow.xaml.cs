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
    public string State{get;set;}="pending";
    public string Metadata{get{if(Analysis is not { } a)return "";string motion=a.MotionFps is { } fps?$"~{fps:0.##}fps":translate("추정 불가","not estimated");
        return $"{a.Source.Width} × {a.Source.Height} · {a.Source.BitDepth}-bit SDR · {translate("파일","File")} {a.Source.Fps.ToDouble():0.##}fps · {translate("움직임","Motion")} {motion}";}}
    public string Status=>State switch{"analyze"=>translate("분석 중","Analyzing"),"ready"=>translate("변환 준비","Ready"),"convert"=>translate("변환 중","Processing"),
        "done"=>translate("검증 통과","Verified"),"unverified"=>translate("변환 완료 · 검증 안 함","Converted · not verified"),"warning"=>translate("검증 경고 · 결과 확인 필요","Quality warning · review output"),
        "error"=>translate("오류 · 다시 시도 가능","Error · retry available"),"cancelled"=>translate("취소됨","Cancelled"),_=>translate("대기","Pending")};
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Refresh(){PropertyChanged?.Invoke(this,new(null));}
}

public partial class MainWindow:Window
{
    private readonly ConversionPipeline pipeline=new(AppPaths.Discover());
    public ObservableCollection<QueueEntry> Entries{get;}=new();
    private HardwareInventory? inventory;
    private List<EncoderCapability> encoders=new();
    private Preferences preferences=Preferences.Load();
    private CancellationTokenSource? work;
    private bool busy,ready,updating,closing;
    private Forms.NotifyIcon? tray;
    private readonly CancellationTokenSource lifetime=new();
    public bool English=>Selected(LanguageBox)=="en";
    public bool HardwareReady=>ready;
    public void SelfTestOutputFolder(string path)=>OutputBox.Text=path;
    private string T(string ko,string en)=>English?en:ko;
    private static string Selected(ComboBox box)=>(box.SelectedItem as Choice)?.Value??"auto";
    private static void Select(ComboBox box,string value)=>box.SelectedItem=box.Items.OfType<Choice>().FirstOrDefault(x=>x.Value==value)??box.Items.OfType<Choice>().FirstOrDefault();
    private void Fill(ComboBox box,IEnumerable<Choice> choices,string? value=null){string selected=value??Selected(box);box.ItemsSource=choices.ToArray();Select(box,selected);}
    public MainWindow(){
        InitializeComponent();Style=(Style)FindResource(typeof(Window));QueueList.ItemsSource=Entries;UpdatesButton.HorizontalAlignment=HorizontalAlignment.Left;
        updating=true;Fill(LanguageBox,[new("ko","한국어"),new("en","English")],preferences.Language);
        Fill(ModelBox,[new("v4.25","RIFE 4.25"),new("v4.26","RIFE 4.26")],preferences.Model);
        OutputBox.Text=preferences.OutputDirectory;QualitySlider.Value=Math.Clamp(preferences.Quality,1,51);
        HardwareCheck.IsChecked=preferences.HardwareEncoding;VerifyCheck.IsChecked=preferences.Verify;
        Translate();Select(PresetBox,preferences.Preset);Select(FpsBox,preferences.Fps);updating=false;
        StateChanged+=(_,_)=>{if(WindowState==WindowState.Minimized&&tray is not null)Hide();};
    }
    public async Task InitializeAsync(bool useTray=true){
        try{
            var data=await Task.Run(()=>pipeline.HardwareAsync(lifetime.Token));inventory=data.Hardware;encoders=data.Encoders;
            updating=true;RefreshOptions();Select(DeviceBox,preferences.Device);
            if(Selected(DeviceBox)!="auto"&&Selected(DeviceBox)!="cpu"&&preferences.DeviceName!=((DeviceBox.SelectedItem as Choice)?.Label??""))Select(DeviceBox,"auto");
            Select(EncoderBox,preferences.Encoder);ready=true;updating=false;
            UpdateHardwareText();
            Log(T("실행 가능한 장치와 인코더를 확인했습니다.","Available devices and encoders checked."));
            if(useTray)SetupTray();
        }catch(Exception ex){Log(ex.Message);HardwareText.Text=ex.Message;updating=false;}
        UpdateControls();
    }
    private void Translate(){
        bool old=updating;updating=true;
        Tagline.Text=T("프레임 사이를, 움직임으로.","Make room for motion.");AddButton.Content=T("영상 추가","Add videos");QueueTitle.Text=T("입력 영상","Input videos");
        DropText.Text=T("영상을 끌어놓으세요","Drop your videos here");DropHint.Text=T("30fps 원본과 60fps로 녹화된 게임 영상","30fps originals and 60fps game captures");
        SettingsTitle.Text=T("출력 설정","Output settings");OutputLabel.Text=T("저장 폴더","Output folder");PresetLabel.Text=T("프리셋","Preset");
        FpsLabel.Text=T("목표 프레임레이트","Output frame rate");CodecLabel.Text=T("코덱","Codec");DeviceLabel.Text=T("추론 장치","Inference device");EncoderLabel.Text=T("인코더","Encoder");
        QualityLabel.Text=T("품질 (낮을수록 고화질)","Quality (lower is better)");BitrateLabel.Text=T("목표 비트레이트 (예: 24M, 선택)","Target bitrate (e.g. 24M, optional)");
        HudLabel.Text=T("고정 HUD 영역 (X,Y,W,H; 선택)","Static HUD region (X,Y,W,H; optional)");ModelLabel.Text=T("RIFE 모델","RIFE model");
        Advanced.Header=T("고급 설정","Advanced");HardwareCheck.Content=T("하드웨어 인코딩 사용","Use hardware encoding");VerifyCheck.Content=T("변환 후 품질·동기 검증","Verify quality and synchronization");
        RemoveButton.Content=T("선택 제거","Remove");StartButton.Content=T("변환 시작","Convert");CancelButton.Content=T("취소","Cancel");
        OpenButton.Content=T("결과 파일 열기","Open result");FolderButton.Content=T("출력 폴더 열기","Open folder");UpdatesButton.Content=T("업데이트","Updates");
        OutputBox.ToolTip=T("비워 두면 원본 영상과 같은 폴더에 저장합니다.","Leave empty to save beside the source video.");
        foreach(var (control,name) in new (UIElement,string)[]{(OutputBox,OutputLabel.Text),(FpsBox,FpsLabel.Text),(CodecBox,CodecLabel.Text),(DeviceBox,DeviceLabel.Text),
            (EncoderBox,EncoderLabel.Text),(ModelBox,ModelLabel.Text),(QualitySlider,QualityLabel.Text),(BitrateBox,BitrateLabel.Text),(HudBox,HudLabel.Text)})
            System.Windows.Automation.AutomationProperties.SetName(control,name);
        Fill(PresetBox,[new("youtube","YouTube"),new("quality",T("품질 우선","Quality"))]);
        Fill(FpsBox,[new("auto",T("입력에 맞춤 (권장)","Match input (recommended)")),new("60","60fps"),new("60000/1001","59.94fps"),new("120","120fps"),new("120000/1001","119.88fps")]);
        RefreshOptions();foreach(var entry in Entries)entry.Refresh();updating=old;UpdateControls();
        UpdateHardwareText();
        if(tray?.ContextMenuStrip is { } menu){menu.Items[0].Text=T("창 열기","Open window");menu.Items[2].Text=T("업데이트","Updates");menu.Items[3].Text=T("종료","Exit");}
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
        if(stage=="fallback")return "CPU fallback: inference can be much slower.";
        if(stage=="encoder_fallback")return "Retrying with software encoding; performance can be lower.";
        if(stage=="hardware")return "Inspecting device/encoder capabilities and transfer cost.";
        return message.Replace("FP16 저장·FP32 연산","FP16 storage / FP32 arithmetic").Replace("FP32 저장·연산","FP32 storage / arithmetic")
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
        var codecs=encoders.Where(x=>x.Available&&(!deep||x.TenBit)&&(hw||!x.Hardware)).Select(x=>x.Codec).Distinct().ToHashSet();
        Fill(CodecBox,new[]{new Choice("auto",T("자동 (지원 기능에 맞춤)","Automatic (supported format)"))}.Concat(new[]{"h264","hevc","av1"}.Where(codecs.Contains).Select(x=>new Choice(x,x=="h264"?"H.264":x.ToUpperInvariant()))));
        string? codec=Selected(CodecBox)=="auto"?null:Selected(CodecBox);
        try{codec??=EncoderPolicy.DefaultCodec(encoders,Selected(PresetBox)=="youtube",deep,hw);}catch(InvalidOperationException){}
        Fill(EncoderBox,new[]{new Choice("auto",T("자동 선택","Automatic"))}.Concat(encoders.Where(x=>x.Available&&x.Codec==codec&&(!deep||x.TenBit)&&(hw||!x.Hardware))
            .Select(x=>new Choice(x.Name,x.Name+(x.Hardware?"":T(" · 소프트웨어"," · software"))))));
        updating=old;
    }
    private void LanguageChanged(object sender,SelectionChangedEventArgs e){if(!updating&&IsInitialized)Translate();}
    private void OptionsChanged(object sender,RoutedEventArgs e){if(!updating&&IsInitialized)RefreshOptions();}
    private async void AddClicked(object sender,RoutedEventArgs e){var dialog=new OpenFileDialog{Multiselect=true,Filter="Video|*.mp4;*.mkv;*.mov;*.ts|All files|*.*"};if(dialog.ShowDialog(this)==true)await AddFilesAsync(dialog.FileNames);}
    public async Task AddFilesAsync(IEnumerable<string> files){
        if(busy)return;busy=true;work=new CancellationTokenSource();StatusText.Text=T("영상 분석 중","Analyzing video");UpdateControls();
        try{foreach(string file in files){
            if(work.IsCancellationRequested)break;
            if(!File.Exists(file)||Entries.Any(x=>string.Equals(x.Input,file,StringComparison.OrdinalIgnoreCase)))continue;
            var entry=new QueueEntry(Path.GetFullPath(file),T){State="analyze"};Entries.Add(entry);QueueList.SelectedItem=entry;UpdateControls();
            try{entry.Analysis=await Task.Run(()=>pipeline.AnalyzeAsync(entry.Input,cancel:work.Token));entry.State="ready";}
            catch(OperationCanceledException){entry.State="cancelled";}catch(Exception ex){entry.State="error";Log(entry.Name+": "+ex.Message);}
            entry.Refresh();
        }}finally{busy=false;work.Dispose();work=null;StatusText.Text=T("변환 준비","Ready");RefreshOptions();UpdateControls();if(closing)Close();}
    }
    public async Task RunQueueAsync(){
        if(busy||!ready)return;
        busy=true;work=new CancellationTokenSource();UpdateControls();
        try{
            var options=new ConversionOptions{Youtube=Selected(PresetBox)=="youtube",Fps=Selected(FpsBox)=="auto"?null:Rational.Parse(Selected(FpsBox)),
                Codec=Selected(CodecBox)=="auto"?null:Selected(CodecBox),Device=Selected(DeviceBox),Encoder=Selected(EncoderBox),Model=Selected(ModelBox),
                Quality=(int)QualitySlider.Value,Bitrate=string.IsNullOrWhiteSpace(BitrateBox.Text)?null:BitrateBox.Text.Trim(),
                HardwareEncoding=HardwareCheck.IsChecked==true,Verify=VerifyCheck.IsChecked==true,Hud=ParseHud()};
            foreach(var entry in Entries.Where(x=>x.Output is null&&x.Analysis is not null).ToArray()){
                work.Token.ThrowIfCancellationRequested();QueueList.SelectedItem=entry;entry.State="convert";entry.Refresh();
                string folder=string.IsNullOrWhiteSpace(OutputBox.Text)?Path.GetDirectoryName(entry.Input)!:Path.GetFullPath(OutputBox.Text);
                string stem=Path.GetFileNameWithoutExtension(entry.Input)+"_smooth";string output=Path.Combine(folder,stem+".mp4");int index=2;
                while(File.Exists(output))output=Path.Combine(folder,stem+"_"+index+++".mp4");
                var progress=new Progress<ConversionProgress>(p=>{
                    ProgressBar.Value=p.Fraction;StatusText.Text=Stage(p.Stage);
                    RateText.Text=p.Frames>0?$"{p.Frames:N0} / {p.Total:N0} · {T("처리","Processing")} {p.Frames/Math.Max(.01,p.Seconds):0.#}fps"+
                        (p.Stage=="convert"&&p.Remaining is { } remaining?$" · {T("남음","Remaining")} {TimeSpan.FromSeconds(remaining):hh\\:mm\\:ss}":""):"";
                    if(!string.IsNullOrEmpty(p.Message))Log(UserMessage(p.Message,p.Stage));
                });
                try{
                    var result=await Task.Run(()=>pipeline.RunAsync(entry.Input,output,options,progress,work.Token));
                    entry.Output=result.Output;entry.State=result.Verification is null?"unverified":result.Verification.Ok?"done":"warning";
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
        "convert"=>T("보간·인코딩 중","Interpolating and encoding"),"mux"=>T("마무리 중","Finalizing"),"verify"=>T("품질·동기 검증 중","Verifying quality and sync"),
        "done"=>T("변환 완료","Conversion complete"),"warning"=>T("검증 경고 · 결과를 확인하세요","Quality warning · review the output"),"fallback"=>T("CPU 대체 경로 사용","Using CPU fallback"),"encoder_fallback"=>T("소프트웨어 인코딩으로 대체","Software encoding fallback"),_=>stage};
    private void UpdateControls(){
        if(!IsInitialized)return;
        EmptyPanel.Visibility=Entries.Count==0?Visibility.Visible:Visibility.Collapsed;AddButton.IsEnabled=!busy;RemoveButton.IsEnabled=!busy&&QueueList.SelectedItem is not null;
        SettingsPanel.IsEnabled=!busy;LanguageBox.IsEnabled=!busy;CancelButton.IsEnabled=work is not null;
        StartButton.IsEnabled=ready&&!busy&&Entries.Any(x=>x.Analysis is not null&&x.Output is null);
        OpenButton.IsEnabled=(QueueList.SelectedItem as QueueEntry)?.Output is { } output&&File.Exists(output);
        if(!busy&&Entries.Count==0)StatusText.Text=T("영상을 추가해 주세요.","Add a video to begin.");
    }
    private void Log(string text){LogBox.AppendText(text+Environment.NewLine);LogBox.ScrollToEnd();}
    private async void StartClicked(object sender,RoutedEventArgs e)=>await RunQueueAsync();
    private void CancelClicked(object sender,RoutedEventArgs e)=>work?.Cancel();
    private void RemoveClicked(object sender,RoutedEventArgs e){if(!busy&&QueueList.SelectedItem is QueueEntry item)Entries.Remove(item);RefreshOptions();UpdateControls();}
    private void QueueChanged(object sender,SelectionChangedEventArgs e)=>UpdateControls();
    private void ChooseFolder(object sender,RoutedEventArgs e){var dialog=new OpenFolderDialog();if(dialog.ShowDialog(this)==true)OutputBox.Text=dialog.FolderName;}
    private static void Open(string path)=>Process.Start(new ProcessStartInfo(path){UseShellExecute=true});
    private void OpenClicked(object sender,RoutedEventArgs e){if(QueueList.SelectedItem is QueueEntry{Output:not null} item)Open(item.Output);}
    private void OpenFolderClicked(object sender,RoutedEventArgs e){string? folder=(QueueList.SelectedItem as QueueEntry)?.Output is { } output?Path.GetDirectoryName(output):OutputBox.Text;if(!string.IsNullOrEmpty(folder)&&Directory.Exists(folder))Open(folder);}
    private void UpdatesClicked(object sender,RoutedEventArgs e)=>Open("https://github.com/deuxdoom/FrameDoubler/releases/latest");
    private async void OnDrop(object sender,DragEventArgs e){if(e.Data.GetData(DataFormats.FileDrop) is string[] files)await AddFilesAsync(files);}
    private void OnDragOver(object sender,DragEventArgs e){e.Effects=!busy&&e.Data.GetDataPresent(DataFormats.FileDrop)?DragDropEffects.Copy:DragDropEffects.None;e.Handled=true;}
    private void SetupTray(){
        var stream=System.Windows.Application.GetResourceStream(new Uri("Assets/app.ico",UriKind.Relative))!.Stream;
        tray=new Forms.NotifyIcon{Icon=new System.Drawing.Icon(stream),Text="FrameDoubler",Visible=true};
        var menu=new Forms.ContextMenuStrip();menu.Items.Add(T("창 열기","Open window"),null,(_,_)=>Restore());
        menu.Items.Add("GitHub",null,(_,_)=>Open("https://github.com/deuxdoom/FrameDoubler"));menu.Items.Add(T("업데이트","Updates"),null,(_,_)=>Open("https://github.com/deuxdoom/FrameDoubler/releases/latest"));
        menu.Items.Add(T("종료","Exit"),null,(_,_)=>Close());tray.ContextMenuStrip=menu;tray.DoubleClick+=(_,_)=>Restore();
    }
    private void Restore(){Show();WindowState=WindowState.Normal;Activate();}
    private void Save(){
        try{preferences=new(){Language=Selected(LanguageBox),OutputDirectory=OutputBox.Text,Preset=Selected(PresetBox),Fps=Selected(FpsBox),Codec=Selected(CodecBox),
            Device=Selected(DeviceBox),DeviceName=(DeviceBox.SelectedItem as Choice)?.Label??"",Encoder=Selected(EncoderBox),Model=Selected(ModelBox),Quality=(int)QualitySlider.Value,
            Verify=VerifyCheck.IsChecked==true,HardwareEncoding=HardwareCheck.IsChecked==true};preferences.Save();}catch(Exception ex){Log(ex.Message);}
    }
    private void OnClosing(object? sender,CancelEventArgs e){if(busy){e.Cancel=true;closing=true;work?.Cancel();return;}lifetime.Cancel();Save();tray?.Dispose();}
    public void PreviewLanguage(string language){Select(LanguageBox,language);Translate();}
}
