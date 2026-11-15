using System;
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

// Shown only when a newer release exists ("up to date" is a compact BrandDialog instead).
// It lists the release notes and walks through download → integrity check → keep settings → restart.
public sealed class UpdateWindow:Window
{
    private enum StepState{Pending,Active,Done,Failed}
    private sealed record Step(Border Marker,TextBlock Mark,TextBlock Label,TextBlock Detail);
    private readonly CancellationTokenSource stop=new();
    private readonly UpdateRelease release;
    private readonly bool english;
    private readonly ProgressBar progress=new(){Maximum=1,Height=6,Margin=new Thickness(30,6,0,4),Visibility=Visibility.Collapsed};
    private readonly TextBlock status=new(){Margin=new Thickness(0,10,0,0),TextWrapping=TextWrapping.Wrap};
    private readonly Button install=new(){MinWidth=132};
    private readonly Step download,verify,preserve,restart;
    private string T(string ko,string en)=>english?en:ko;
    private static SolidColorBrush Brush(string value)=>(SolidColorBrush)new BrushConverter().ConvertFromString(value)!;
    // Diagnostics only ("--update-test"): a local package replaces the GitHub download; everything after
    // it (verification, installer handoff, exit, replacement, restart) is the normal path.
    internal string? LocalPackage{get;init;}
    private readonly MainWindow owner;
    internal Task InstallNowAsync()=>InstallAsync(owner);
    internal string? LastError{get;private set;}
    public UpdateWindow(MainWindow owner,UpdateRelease release,bool preview=false){
        this.release=release;this.owner=owner;
        if(!preview)Owner=owner;english=owner.English;Title=T("FrameDoubler 업데이트","FrameDoubler update");Width=720;Height=620;MinWidth=600;MinHeight=520;
        Style=(Style)FindResource(typeof(Window));WindowStartupLocation=WindowStartupLocation.CenterOwner;
        WindowStyle=WindowStyle.None;ResizeMode=ResizeMode.CanResize;
        WindowChrome.SetWindowChrome(this,new WindowChrome{CaptionHeight=48,ResizeBorderThickness=new Thickness(6),GlassFrameThickness=new Thickness(0),CornerRadius=new CornerRadius(12),UseAeroCaptionButtons=false});
        SourceInitialized+=(_,_)=>WindowFrame.Attach(this);
        install.Style=(Style)FindResource("PrimaryButton");
        progress.Foreground=Brush("#FF8C60");progress.Background=(Brush)FindResource("SurfaceBrush");progress.BorderThickness=new Thickness(0);

        var grid=new Grid{Margin=new Thickness(24,4,24,22)};
        foreach(var height in new[]{GridLength.Auto,GridLength.Auto,new GridLength(1,GridUnitType.Star),GridLength.Auto,GridLength.Auto,GridLength.Auto})grid.RowDefinitions.Add(new(){Height=height});

        // Version header.
        var header=new Grid{Margin=new Thickness(0,0,0,18)};header.ColumnDefinitions.Add(new(){Width=GridLength.Auto});header.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});
        header.Children.Add(new Border{Width=44,Height=44,CornerRadius=new CornerRadius(13),Background=Brush("#30271F"),Margin=new Thickness(0,0,16,0),
            Child=new TextBlock{Text=BrandDialog.Glyphs.Update,FontFamily=new FontFamily("Segoe Fluent Icons"),FontSize=20,Foreground=Brush("#FF8C60"),HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center}});
        var heading=new StackPanel{VerticalAlignment=VerticalAlignment.Center};
        heading.Children.Add(new TextBlock{Text=Hangul.KeepWords(T($"새 버전 {release.Version}을 설치할 수 있습니다",$"FrameDoubler {release.Version} is available")),FontSize=18,FontWeight=FontWeights.SemiBold});
        heading.Children.Add(new TextBlock{Text=T($"현재 {Updates.Current} → {release.Version}",$"Current {Updates.Current} → {release.Version}")+(release.Size>0?$" · {release.Size/1048576d:0} MB":""),Foreground=(Brush)FindResource("MutedBrush"),Margin=new Thickness(0,4,0,0)});
        Grid.SetColumn(heading,1);header.Children.Add(heading);grid.Children.Add(header);

        var notesLabel=new TextBlock{Text=T("변경 사항","What's new"),Style=(Style)FindResource("FieldLabel"),Margin=new Thickness(0,0,0,6)};
        Grid.SetRow(notesLabel,1);grid.Children.Add(notesLabel);
        string summary=Updates.ReadableNotes(release.Notes);
        var notes=new TextBox{IsReadOnly=true,TextWrapping=TextWrapping.Wrap,VerticalContentAlignment=VerticalAlignment.Top,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,MinHeight=150,
            Text=Hangul.KeepWords(summary.Length>0?summary:T("변경 사항은 릴리스 페이지에서 확인할 수 있습니다.","See the release page for the changes."))};
        Grid.SetRow(notes,2);grid.Children.Add(notes);

        // Install steps.
        var steps=new StackPanel{Margin=new Thickness(0,18,0,0)};
        steps.Children.Add(new TextBlock{Text=T("설치 단계","Installation steps"),Style=(Style)FindResource("FieldLabel"),Margin=new Thickness(0,0,0,8)});
        string asset=Updates.AssetName(release.Version);
        download=AddStep(steps,T("다운로드","Download"),T($"GitHub 릴리스에서 {asset} 파일을 받습니다.",$"Gets {asset} from the GitHub release."));
        steps.Children.Add(progress);
        verify=AddStep(steps,T("무결성 확인","Integrity check"),T("SHA-256과 패키지 안 모든 파일의 해시를 확인합니다.","Checks the SHA-256 and the hash of every packaged file."));
        preserve=AddStep(steps,T("설정 보존","Keep settings"),T("설정과 성능 기록을 새 버전으로 옮깁니다.","Carries settings and performance history over."));
        restart=AddStep(steps,T("재시작·설치","Restart and install"),T("프로그램을 종료하고 파일을 교체합니다. 실패하면 이전 버전으로 되돌립니다.","Closes FrameDoubler and replaces the files, rolling back on failure."));
        steps.Children.Add(status);
        Grid.SetRow(steps,3);grid.Children.Add(steps);

        var buttons=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,18,0,0)};
        var page=new Button{Content=T("릴리스 페이지","Release page"),Style=(Style)FindResource("TextButton"),Padding=new Thickness(12,5,12,5),Margin=new Thickness(0,0,10,0)};
        page.Click+=(_,_)=>Process.Start(new ProcessStartInfo(release.Page){UseShellExecute=true});
        var later=new Button{Content=T("나중에","Later"),Style=(Style)FindResource("SecondaryButton"),MinWidth=96,Margin=new Thickness(0,0,10,0)};later.Click+=(_,_)=>Close();
        install.Content=T("업데이트 설치","Install update");install.Click+=async(_,_)=>await InstallAsync(owner);
        buttons.Children.Add(page);buttons.Children.Add(later);buttons.Children.Add(install);Grid.SetRow(buttons,5);grid.Children.Add(buttons);

        var frame=new Grid();frame.RowDefinitions.Add(new(){Height=new GridLength(48)});frame.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});
        var caption=new Grid();
        var name=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(20,0,60,0),VerticalAlignment=VerticalAlignment.Center};
        name.Children.Add(new Image{Source=new BitmapImage(new Uri("pack://application:,,,/Assets/icon.png")),Width=18,Height=18,Margin=new Thickness(0,0,10,0)});
        name.Children.Add(new TextBlock{Text=Title,VerticalAlignment=VerticalAlignment.Center,FontWeight=FontWeights.SemiBold});caption.Children.Add(name);
        var close=new Button{Content="",FontFamily=new FontFamily("Segoe Fluent Icons"),Style=(Style)FindResource("CloseCaptionButton"),HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,0,6,0),ToolTip=T("닫기","Close")};
        WindowChrome.SetIsHitTestVisibleInChrome(close,true);close.Click+=(_,_)=>Close();caption.Children.Add(close);frame.Children.Add(caption);
        Grid.SetRow(grid,1);frame.Children.Add(grid);
        Content=new Border{Background=Background,BorderBrush=Brush("#30373C"),BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(12),Child=frame};
        Closed+=(_,_)=>stop.Cancel();

        foreach(var step in new[]{download,verify,preserve,restart})Set(step,StepState.Pending);
        if(!release.Installable){install.IsEnabled=false;status.Foreground=Brush("#FFB08C");
            status.Text=Hangul.KeepWords(T("검증된 업데이트 파일이 없어 자동으로 설치할 수 없습니다. 릴리스 페이지에서 확인하세요.","No verified update package is attached, so it can't be installed automatically. Use the release page."));}
    }
    private Step AddStep(Panel parent,string label,string detail){
        var row=new Grid{Margin=new Thickness(0,4,0,4)};row.ColumnDefinitions.Add(new(){Width=new GridLength(30)});row.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});
        var mark=new TextBlock{FontFamily=new FontFamily("Segoe Fluent Icons"),FontSize=10,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center};
        var marker=new Border{Width=20,Height=20,CornerRadius=new CornerRadius(10),BorderThickness=new Thickness(1.5),HorizontalAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Top,Margin=new Thickness(0,1,0,0),Child=mark};
        row.Children.Add(marker);
        var text=new StackPanel();var title=new TextBlock{Text=label,FontWeight=FontWeights.SemiBold};var info=new TextBlock{Text=Hangul.KeepWords(detail),Foreground=(Brush)FindResource("MutedBrush"),FontSize=12,Margin=new Thickness(0,2,0,0)};
        text.Children.Add(title);text.Children.Add(info);Grid.SetColumn(text,1);row.Children.Add(text);parent.Children.Add(row);
        return new(marker,mark,title,info);
    }
    private static readonly FontFamily Icons=new("Segoe Fluent Icons"),Text=UiFonts.Text;
    private static void Set(Step step,StepState state,string? detail=null){
        step.Mark.FontFamily=state==StepState.Active?Text:Icons;step.Mark.FontSize=state==StepState.Active?9:10;
        switch(state){
            case StepState.Pending:step.Marker.Background=Brushes.Transparent;step.Marker.BorderBrush=Brush("#60717C");step.Mark.Text="";step.Label.Foreground=Brush("#A6B1B8");break;
            case StepState.Active:step.Marker.Background=Brush("#30271F");step.Marker.BorderBrush=Brush("#FF8C60");step.Mark.Text="●";step.Mark.Foreground=Brush("#FF8C60");step.Label.Foreground=Brush("#F3F1EB");break;
            case StepState.Done:step.Marker.Background=Brush("#FF8C60");step.Marker.BorderBrush=Brush("#FF8C60");step.Mark.Text="";step.Mark.Foreground=Brush("#14181B");step.Label.Foreground=Brush("#F3F1EB");break;
            case StepState.Failed:step.Marker.Background=Brush("#BF3F3F");step.Marker.BorderBrush=Brush("#BF3F3F");step.Mark.Text="";step.Mark.Foreground=Brush("#F3F1EB");step.Label.Foreground=Brush("#F3F1EB");break;
        }
        if(detail is not null)step.Detail.Text=Hangul.KeepWords(detail);
    }
    private async Task InstallAsync(MainWindow owner){
        install.IsEnabled=false;status.Text="";status.ClearValue(TextBlock.ForegroundProperty);
        foreach(var step in new[]{download,verify,preserve,restart})Set(step,StepState.Pending);
        Step current=download;StagedUpdate? staged=null;UpdateInstaller.InstallerSession? session=null;bool committed=false;
        try{
            Set(download,StepState.Active);progress.Value=0;progress.Visibility=Visibility.Visible;
            string location=Path.TrimEndingDirectorySeparator(AppPaths.InstallationDirectory),parent=Path.GetDirectoryName(location)??throw new IOException(T("드라이브 루트에 설치된 경우 자동 업데이트를 할 수 없습니다.","Automatic updates need an installation folder, not a drive root."));
            // Progress reaches 1 when the download ends; StageAsync then checks the SHA-256 and every file.
            staged=LocalPackage is not null?await Updates.StageAsync(LocalPackage,release.Sha256!,release.Version,parent,stop.Token):await Updates.DownloadAsync(release,parent,new Progress<double>(x=>{
                progress.Value=x;
                if(current==download)download.Detail.Text=release.Size>0?$"{x*release.Size/1048576d:0} / {release.Size/1048576d:0} MB · {x:P0}":$"{x:P0}";
                if(x>=1&&current==download){Set(download,StepState.Done);progress.Visibility=Visibility.Collapsed;current=verify;Set(verify,StepState.Active);}
            }),stop.Token);
            stop.Token.ThrowIfCancellationRequested();
            if(current==download){Set(download,StepState.Done);progress.Visibility=Visibility.Collapsed;}
            Set(verify,StepState.Done,T("SHA-256과 모든 파일의 해시가 일치합니다.","SHA-256 and every file hash match."));
            current=preserve;Set(preserve,StepState.Active,T("설치 프로그램을 준비하고 설정을 옮깁니다.","Preparing the installer and carrying settings over."));
            // The installer runs from its own copy beside the installation. It checks the package again and
            // confirms that it is ready; only then are settings copied and the app allowed to exit.
            session=await UpdateInstaller.PrepareAsync(staged,location,english,stop.Token);
            owner.SaveForUpdate();TimeEstimates.PreservePortableFiles(location,staged.Directory);
            stop.Token.ThrowIfCancellationRequested();
            Set(preserve,StepState.Done);
            current=restart;Set(restart,StepState.Active,T("프로그램을 종료하면 설치 창에서 파일을 교체합니다.","FrameDoubler closes and the installer window replaces the files."));
            session.Commit();committed=true;
            owner.AllowExit();owner.Close();Close();
        }catch(OperationCanceledException){LastError="cancelled";Set(current,StepState.Failed,T("취소됨","Cancelled"));progress.Visibility=Visibility.Collapsed;}
        catch(Exception ex){
            LastError=ex.ToString();
            Set(current,StepState.Failed,ex.Message);progress.Visibility=Visibility.Collapsed;
            status.Foreground=Brush("#FFB08C");status.Text=Hangul.KeepWords(T("업데이트를 설치하지 못했습니다. 현재 버전은 그대로 유지됩니다.","The update could not be installed. The current version is unchanged."));
            install.Content=T("다시 시도","Try again");install.IsEnabled=!stop.IsCancellationRequested;
        }finally{
            // Not committed: the installer is told to stop and the downloaded copy is removed.
            if(!committed){session?.Dispose();if(staged is not null)UpdateInstaller.DiscardStage(staged);}else session?.Dispose();
        }
    }
}
