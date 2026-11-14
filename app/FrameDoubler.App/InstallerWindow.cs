using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using FrameDoubler.Core;

namespace FrameDoubler.App;

// Runs from the copied runner beside the installation ("--apply-update <request>"), without the main
// window, settings or device detection. It stays hidden while the old app is still deciding (verify →
// ready → commit) and appears once the old app is closing, so a failure is always reported somewhere.
// Files are only moved after the old process has exited; the window cannot be closed while they move.
public sealed class InstallerWindow:Window
{
    private enum StepState{Pending,Active,Done,Failed}
    private sealed record Step(string Phase,Border Marker,TextBlock Mark,TextBlock Label,TextBlock Detail);
    private readonly string request,runner;
    private readonly bool english;
    private readonly CancellationTokenSource cancel=new();
    private readonly TextBlock heading=new(){FontSize=17,FontWeight=FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap};
    private readonly TextBlock body=new(){Margin=new Thickness(0,6,0,0),TextWrapping=TextWrapping.Wrap,LineHeight=20};
    private readonly TextBlock notice=new(){TextWrapping=TextWrapping.Wrap,LineHeight=19};
    private readonly Border noticeBox=new(){BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(8),Padding=new Thickness(12,9,12,9),Margin=new Thickness(0,14,0,0),Visibility=Visibility.Collapsed};
    private readonly ProgressBar progress=new(){Height=4,IsIndeterminate=true,Margin=new Thickness(0,16,0,0)};
    private readonly Button stopButton=new(){MinWidth=96},logButton=new(),closeButton=new(){MinWidth=104};
    private readonly Button captionClose=new(){Content="",FontFamily=new FontFamily("Segoe Fluent Icons"),HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,0,4,0),IsEnabled=false};
    private readonly Step[] steps;
    private string phase="prepare";
    private int reached=-1;
    private bool finished;
    public InstallResult? Result{get;private set;}
    // Set once the handoff happened: before that the old app still owns the error message.
    public bool Presented{get;private set;}
    private string T(string ko,string en)=>english?en:ko;
    private static SolidColorBrush Brush(string value)=>(SolidColorBrush)new BrushConverter().ConvertFromString(value)!;
    public InstallerWindow(string request,string runner,bool english){
        this.request=request;this.runner=runner;this.english=english;
        Title=T("FrameDoubler 업데이트 설치","Installing FrameDoubler update");Width=520;SizeToContent=SizeToContent.Height;ResizeMode=ResizeMode.NoResize;
        Style=(Style)FindResource(typeof(Window));WindowStyle=WindowStyle.None;WindowStartupLocation=WindowStartupLocation.CenterScreen;
        WindowChrome.SetWindowChrome(this,new WindowChrome{CaptionHeight=40,ResizeBorderThickness=new Thickness(0),GlassFrameThickness=new Thickness(0),CornerRadius=new CornerRadius(12),UseAeroCaptionButtons=false});
        SourceInitialized+=(_,_)=>WindowFrame.RoundCorners(this);
        progress.Foreground=Brush("#FF8C60");progress.Background=(Brush)FindResource("SurfaceBrush");progress.BorderThickness=new Thickness(0);
        body.Foreground=Brush("#A6B1B8");

        var caption=new Grid{Height=40};
        var name=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(16,0,0,0),VerticalAlignment=VerticalAlignment.Center};
        name.Children.Add(new Image{Source=new BitmapImage(new Uri("pack://application:,,,/Assets/icon.png")),Width=16,Height=16,Margin=new Thickness(0,0,8,0)});
        name.Children.Add(new TextBlock{Text=Title,Foreground=Brush("#A6B1B8"),FontSize=12,VerticalAlignment=VerticalAlignment.Center});caption.Children.Add(name);
        captionClose.Style=(Style)FindResource("CloseCaptionButton");captionClose.ToolTip=T("닫기","Close");
        WindowChrome.SetIsHitTestVisibleInChrome(captionClose,true);captionClose.Click+=(_,_)=>Close();caption.Children.Add(captionClose);

        var content=new Grid{Margin=new Thickness(24,8,24,22)};
        content.ColumnDefinitions.Add(new(){Width=GridLength.Auto});content.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});
        content.Children.Add(new Border{Width=44,Height=44,CornerRadius=new CornerRadius(13),Background=Brush("#30271F"),VerticalAlignment=VerticalAlignment.Top,Margin=new Thickness(0,0,16,0),
            Child=new TextBlock{Text=BrandDialog.Glyphs.Update,FontFamily=new FontFamily("Segoe Fluent Icons"),FontSize=20,Foreground=Brush("#FF8C60"),HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center}});
        var text=new StackPanel();text.Children.Add(heading);text.Children.Add(body);text.Children.Add(progress);
        var list=new StackPanel{Margin=new Thickness(0,16,0,0)};
        steps=[
            AddStep(list,"waiting",T("종료 대기","Waiting for exit"),T("FrameDoubler가 완전히 종료되기를 기다립니다.","Waits until FrameDoubler has fully exited.")),
            AddStep(list,"backup",T("현재 버전 백업","Back up current version"),T("지금 설치된 폴더를 그대로 옮겨 둡니다.","Moves the installed folder aside unchanged.")),
            AddStep(list,"install",T("새 버전 설치","Install new version"),T("검증을 마친 새 파일을 설치 위치로 옮깁니다.","Moves the verified files into place.")),
            AddStep(list,"starting",T("새 버전 시작","Start new version"),T("새 버전이 정상적으로 열리는지 확인합니다.","Confirms that the new version opens normally.")),
        ];
        text.Children.Add(list);
        notice.Foreground=Brush("#FFB08C");noticeBox.Background=Brush("#2A211B");noticeBox.BorderBrush=Brush("#5A3A2A");noticeBox.Child=notice;text.Children.Add(noticeBox);
        var buttons=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,22,0,0)};
        logButton.Content=T("기록 위치 열기","Open log folder");logButton.Style=(Style)FindResource("TextButton");logButton.Padding=new Thickness(12,5,12,5);logButton.Margin=new Thickness(0,0,10,0);logButton.Visibility=Visibility.Collapsed;
        logButton.Click+=(_,_)=>OpenLogFolder();
        stopButton.Content=T("설치 취소","Cancel");stopButton.Style=(Style)FindResource("SecondaryButton");stopButton.Padding=new Thickness(16,8,16,8);
        stopButton.Click+=(_,_)=>{stopButton.IsEnabled=false;cancel.Cancel();body.Text=Hangul.KeepWords(T("취소하는 중입니다. 파일은 바꾸지 않고 이전 버전을 다시 엽니다.","Cancelling. No files are changed and the current version reopens."));};
        closeButton.Content=T("닫기","Close");closeButton.Style=(Style)FindResource("PrimaryButton");closeButton.Padding=new Thickness(16,8,16,8);closeButton.Visibility=Visibility.Collapsed;closeButton.Click+=(_,_)=>Close();
        buttons.Children.Add(logButton);buttons.Children.Add(stopButton);buttons.Children.Add(closeButton);text.Children.Add(buttons);
        Grid.SetColumn(text,1);content.Children.Add(text);
        var layout=new StackPanel();layout.Children.Add(caption);layout.Children.Add(content);
        Content=new Border{Background=Background,BorderBrush=Brush("#30373C"),BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(12),Child=layout};
        heading.Text=Hangul.KeepWords(T("업데이트를 설치하고 있습니다","Installing the update"));
        body.Text=Hangul.KeepWords(T("FrameDoubler가 종료되면 파일을 교체하고 새 버전을 엽니다.","Files are replaced after FrameDoubler exits, then the new version opens."));
        foreach(var step in steps)Set(step,StepState.Pending);
    }
    private Step AddStep(Panel parent,string phase,string label,string detail){
        var row=new Grid{Margin=new Thickness(0,4,0,4)};row.ColumnDefinitions.Add(new(){Width=new GridLength(30)});row.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});
        var mark=new TextBlock{FontFamily=new FontFamily("Segoe Fluent Icons"),FontSize=10,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center};
        var marker=new Border{Width=20,Height=20,CornerRadius=new CornerRadius(10),BorderThickness=new Thickness(1.5),HorizontalAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Top,Margin=new Thickness(0,1,0,0),Child=mark};
        row.Children.Add(marker);
        var text=new StackPanel();var title=new TextBlock{Text=label,FontWeight=FontWeights.SemiBold};var info=new TextBlock{Text=Hangul.KeepWords(detail),Foreground=(Brush)FindResource("MutedBrush"),FontSize=12,Margin=new Thickness(0,2,0,0),TextWrapping=TextWrapping.Wrap};
        text.Children.Add(title);text.Children.Add(info);Grid.SetColumn(text,1);row.Children.Add(text);parent.Children.Add(row);
        return new(phase,marker,mark,title,info);
    }
    private static readonly FontFamily Icons=new("Segoe Fluent Icons"),Plain=new("Segoe UI");
    private static void Set(Step step,StepState state,string? detail=null){
        step.Mark.FontFamily=state==StepState.Active?Plain:Icons;step.Mark.FontSize=state==StepState.Active?9:10;
        switch(state){
            case StepState.Pending:step.Marker.Background=Brushes.Transparent;step.Marker.BorderBrush=Brush("#60717C");step.Mark.Text="";step.Label.Foreground=Brush("#A6B1B8");break;
            case StepState.Active:step.Marker.Background=Brush("#30271F");step.Marker.BorderBrush=Brush("#FF8C60");step.Mark.Text="●";step.Mark.Foreground=Brush("#FF8C60");step.Label.Foreground=Brush("#F3F1EB");break;
            case StepState.Done:step.Marker.Background=Brush("#FF8C60");step.Marker.BorderBrush=Brush("#FF8C60");step.Mark.Text="";step.Mark.Foreground=Brush("#14181B");step.Label.Foreground=Brush("#F3F1EB");break;
            case StepState.Failed:step.Marker.Background=Brush("#BF3F3F");step.Marker.BorderBrush=Brush("#BF3F3F");step.Mark.Text="";step.Mark.Foreground=Brush("#F3F1EB");step.Label.Foreground=Brush("#F3F1EB");break;
        }
        if(detail is not null)step.Detail.Text=Hangul.KeepWords(detail);
    }
    // Cancelling is possible only before the current installation is moved; afterwards the
    // replacement (or its rollback) must run to the end, so closing is refused until then.
    protected override void OnClosing(CancelEventArgs e){if(!finished)e.Cancel=true;base.OnClosing(e);}
    private bool preview;
    private void Present(){if(Presented)return;Presented=true;if(!preview){Show();Activate();}}
    // "--render-preview": draws a phase or a final result without running the installer.
    internal void PreviewState(string phase,InstallResult? result=null){
        preview=true;
        foreach(string step in new[]{"waiting","backup","install","starting"}){Report(new(step,""));if(step==phase)break;}
        if(result is not null){finished=true;ShowFailure(result);}
    }
    private void Report(InstallProgress value){
        if(finished)return;
        if(value.Phase=="retry"){
            body.Text=Hangul.KeepWords(T("다른 프로그램이 파일을 사용하고 있어 잠시 후 다시 시도합니다. 탐색기나 백신 검사가 끝나면 자동으로 이어집니다.","Files are in use; retrying shortly. It continues once Explorer or an antivirus scan lets go."));
            return;
        }
        phase=value.Phase;
        int index=Array.FindIndex(steps,x=>x.Phase==phase);
        if(phase=="waiting")Present();
        if(index>=0){
            reached=index;
            for(int i=0;i<steps.Length;i++)Set(steps[i],i<index?StepState.Done:i==index?StepState.Active:StepState.Pending);
            body.Text=Hangul.KeepWords(phase switch{
                "waiting"=>T("FrameDoubler가 종료되면 파일을 교체하고 새 버전을 엽니다.","Files are replaced after FrameDoubler exits, then the new version opens."),
                "starting"=>T("새 버전을 시작하고 있습니다.","Starting the new version."),
                _=>T("파일을 교체하는 중입니다. 이 창을 닫지 말고 기다려 주세요.","Replacing files. Please keep this window open.")});
        }
        // Once the installed folder is being moved, the update can no longer be cancelled.
        stopButton.IsEnabled=phase is "handoff" or "waiting"&&!cancel.IsCancellationRequested;
        if(phase is "rollback"){Present();body.Text=Hangul.KeepWords(T("문제가 생겨 이전 버전으로 되돌리고 있습니다.","Something went wrong; restoring the previous version."));}
    }
    public async Task<InstallResult> RunAsync(){
        var progressReport=new Progress<InstallProgress>(Report);
        InstallResult result;
        try{result=await Task.Run(()=>UpdateInstaller.Apply(request,runner,progressReport,cancel.Token));}
        catch(Exception ex){result=new InstallResult(false,phase,ex.Message,"",Updates.Current.ToString(3));}
        // Let queued progress reports land before the final state is drawn.
        await Dispatcher.InvokeAsync(()=>{},System.Windows.Threading.DispatcherPriority.Background);
        Result=result;finished=true;
        if(result.Success){
            if(!Presented)return result;
            foreach(var step in steps)Set(step,StepState.Done);
            heading.Text=Hangul.KeepWords(T($"{result.Version} 버전으로 업데이트했습니다",$"Updated to {result.Version}"));
            body.Text=Hangul.KeepWords(T("새 버전이 열렸습니다. 설정과 성능 기록도 그대로 옮겼습니다.","The new version is open with your settings and performance history."));
            progress.Visibility=Visibility.Collapsed;stopButton.Visibility=Visibility.Collapsed;
            await Task.Delay(1500);Close();return result;
        }
        // Failures before the handoff are reported by the update window of the app that is still open.
        if(!Presented&&result.Phase is not ("rolled-back" or "recovery-failed"))return result;
        Present();ShowFailure(result);
        var closed=new TaskCompletionSource();Closed+=(_,_)=>closed.TrySetResult();await closed.Task;
        return result;
    }
    private void ShowFailure(InstallResult result){
        progress.Visibility=Visibility.Collapsed;stopButton.Visibility=Visibility.Collapsed;closeButton.Visibility=Visibility.Visible;logButton.Visibility=Visibility.Visible;
        captionClose.IsEnabled=true;closeButton.Focus();
        // The step that failed is the furthest one reached (rollback phases are not steps).
        Set(steps[Math.Max(reached,0)],StepState.Failed);
        bool cancelled=result.Phase=="cancelled";
        heading.Text=Hangul.KeepWords(cancelled?T("업데이트를 취소했습니다","Update cancelled"):T("업데이트를 설치하지 못했습니다","The update could not be installed"));
        body.Text=cancelled?Hangul.KeepWords(T("파일은 바뀌지 않았고 이전 버전을 다시 열었습니다.","No files were changed and the current version was reopened.")):T("원인: ","Cause: ")+Cause(result.Error);
        noticeBox.Visibility=Visibility.Visible;
        notice.Text=Hangul.KeepWords(result.Phase switch{
            "rolled-back"=>T("이전 버전을 원래 위치로 되돌리고 다시 열었습니다. 설정과 기록은 그대로입니다.","The previous version was restored and reopened. Settings and history are unchanged."),
            "recovery-failed"=>T($"이전 버전을 자동으로 되돌리지 못했습니다. 백업 폴더({result.Backup})를 원래 이름으로 바꾸면 복구됩니다.",$"The previous version could not be restored automatically. Rename the backup folder ({result.Backup}) back to recover."),
            "cancelled"=>T("설치된 파일은 바뀌지 않았습니다.","Installed files were not changed."),
            // Moving the installed folder failed even after 30 seconds of retries.
            _ when reached>=1=>T("설치된 파일은 바뀌지 않았고 이전 버전을 다시 열었습니다. 설치 폴더를 연 탐색기 창이나 백신 검사가 파일을 사용 중이었을 수 있습니다. 잠시 뒤 다시 시도하세요.",
                "Installed files were not changed and the current version was reopened. An Explorer window or antivirus scan may have been using the folder. Try again shortly."),
            // E.g. FrameDoubler did not finish closing in time: it is still the running version.
            _=>T("설치된 파일은 바뀌지 않았습니다.","Installed files were not changed."),
        });
        if(result.Phase=="recovery-failed"){noticeBox.Background=Brush("#3A1F1F");noticeBox.BorderBrush=Brush("#7A3434");}
    }
    // The installer's own messages in Korean; system messages (file access etc.) stay as Windows reports them.
    private string Cause(string error){
        if(english)return error;
        string first=error.Split("; ")[0];
        string? known=first switch{
            "The new version exited before confirming startup"=>"새 버전이 시작을 확인하기 전에 종료되었습니다.",
            "The new version did not confirm startup"=>"새 버전이 제시간에 시작을 확인하지 않았습니다.",
            "Cannot restart the new version"=>"새 버전을 시작하지 못했습니다.",
            "The app did not finish closing. No files were replaced."=>"FrameDoubler가 제시간에 종료되지 않았습니다.",
            "The app exited before confirming the update"=>"FrameDoubler가 업데이트를 확정하기 전에 종료되었습니다.",
            "Update confirmation timed out"=>"업데이트 확정을 기다리는 시간이 초과되었습니다.",
            "An update backup already exists"=>"같은 이름의 업데이트 백업 폴더가 이미 있습니다.",
            _=>null};
        return Hangul.KeepWords(known is null?error:known+(first.Length<error.Length?" ("+error[(first.Length+2)..]+")":""));
    }
    private void OpenLogFolder(){
        try{
            var parsed=System.Text.Json.JsonSerializer.Deserialize<InstallRequest>(File.ReadAllText(request));
            string folder=parsed is not null&&File.Exists(Path.Combine(parsed.Target,"work","updates","last-install.json"))?Path.Combine(parsed.Target,"work","updates"):Path.GetDirectoryName(request)!;
            Process.Start(new ProcessStartInfo("explorer.exe"){ArgumentList={folder},UseShellExecute=false});
        }catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or System.ComponentModel.Win32Exception){}
    }
}
