using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using FrameDoubler.Core;

namespace FrameDoubler.App;

public sealed class UpdateWindow:Window
{
    private readonly CancellationTokenSource stop=new();
    private readonly TextBlock status=new(){Margin=new Thickness(0,10,0,10)};
    private readonly TextBox notes=new(){IsReadOnly=true,TextWrapping=TextWrapping.Wrap,VerticalContentAlignment=VerticalAlignment.Top,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,MinHeight=240};
    private readonly ProgressBar progress=new(){Maximum=1,Height=8,Margin=new Thickness(0,12,0,12)};
    private readonly Button install=new(){IsEnabled=false};
    private UpdateRelease? release;
    private readonly bool english;
    private string T(string ko,string en)=>english?en:ko;
    public UpdateWindow(MainWindow owner,UpdateRelease? known,bool preview=false){
        if(!preview)Owner=owner;english=owner.English;Title=T("FrameDoubler 업데이트","FrameDoubler update");Width=720;Height=540;MinWidth=600;MinHeight=450;
        Style=(Style)FindResource(typeof(Window));WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var grid=new Grid{Margin=new Thickness(24)};grid.RowDefinitions.Add(new(){Height=GridLength.Auto});grid.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});
        grid.RowDefinitions.Add(new(){Height=GridLength.Auto});grid.RowDefinitions.Add(new(){Height=GridLength.Auto});
        grid.Children.Add(status);Grid.SetRow(notes,1);grid.Children.Add(notes);Grid.SetRow(progress,2);grid.Children.Add(progress);
        var buttons=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};
        var page=new Button{Content=T("릴리스 페이지","Release page"),Margin=new Thickness(0,0,10,0)};
        page.Click+=(_,_)=>Process.Start(new ProcessStartInfo(release?.Page??Updates.ReleasePage){UseShellExecute=true});
        install.Content=T("업데이트 설치","Install update");install.Click+=async(_,_)=>await InstallAsync(owner);
        buttons.Children.Add(page);buttons.Children.Add(install);Grid.SetRow(buttons,3);grid.Children.Add(buttons);Content=grid;
        if(known is not null)ShowRelease(known);
        Closed+=(_,_)=>stop.Cancel();
        Loaded+=async(_,_)=>{
            try{status.Text=T("새 버전을 확인하고 있습니다…","Checking for updates…");ShowRelease(known??await Updates.CheckAsync(stop.Token));
            }catch(OperationCanceledException){}catch(Exception ex){status.Text=ex.Message;}
        };
    }
    private void ShowRelease(UpdateRelease? value){
        release=value;status.Text=release is null?T("현재 최신 버전입니다.","You are up to date."):$"{Updates.Current} → {release.Version}";
        notes.Text=release?.Notes??"";install.IsEnabled=release?.Installable==true;
        if(release is {Installable:false})notes.AppendText(Environment.NewLine+T("검증된 네이티브 패키지가 없어 릴리스 페이지에서 확인해야 합니다.","No verified native asset. Use the release page."));
    }
    private async Task InstallAsync(MainWindow owner){
        if(release is null)return;install.IsEnabled=false;
        try{
            status.Text=T("다운로드·무결성 확인 중…","Downloading and verifying…");
            string location=Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory),parent=Path.GetDirectoryName(location)!;
            var staged=await Updates.DownloadAsync(release,parent,new Progress<double>(x=>progress.Value=x),stop.Token);
            stop.Token.ThrowIfCancellationRequested();
            status.Text=T("프로그램을 종료하고 업데이트합니다.","Restarting to install the update.");
            Updates.LaunchInstaller(staged,location);owner.Close();Close();
        }catch(OperationCanceledException){status.Text=T("취소됨","Cancelled");}
        catch(Exception ex){status.Text=ex.Message;install.IsEnabled=true;}
    }
}
