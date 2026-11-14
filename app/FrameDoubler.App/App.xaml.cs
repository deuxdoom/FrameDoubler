using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FrameDoubler.App;

public partial class App:System.Windows.Application
{
    private SingleInstance? instance;
    protected override void OnExit(ExitEventArgs e){instance?.Dispose();base.OnExit(e);}
    protected override async void OnStartup(StartupEventArgs e){
        base.OnStartup(e);
        try{
            // The update runner is a copy of the app outside the installation. It must not take the
            // single-instance slot, read settings or detect devices: it only replaces files.
            if(e.Args.Length>=2&&e.Args[0]=="--apply-update"){await ApplyUpdateAsync(e.Args[1]);return;}
            bool updated=e.Args.Length>=2&&e.Args[0]=="--updated";
            bool diagnostic=e.Args.Length>0&&e.Args[0] is "--ui-test" or "--render-preview" or "--self-test";
            if(!diagnostic){
                instance=new SingleInstance();
                if(!await instance.AcquireOrActivateAsync()){Shutdown(0);return;}
            }
            if(diagnostic&&e.Args.Length>=2&&string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FRAMEDOUBLER_CONFIG_DIR"))){
                string target=Path.GetFullPath(e.Args[1]);string parent=e.Args[0]=="--render-preview"?target:Path.GetDirectoryName(target)!;
                Environment.SetEnvironmentVariable("FRAMEDOUBLER_CONFIG_DIR",Path.Combine(parent,"diagnostic-settings"));
            }
            var window=new MainWindow();MainWindow=window;
            if(instance is not null)window.SourceInitialized+=(_,_)=>instance.Attach(window,window.ActivateExistingWindow);
            // Signing out or shutting down Windows must not wait for the exit confirmation.
            SessionEnding+=(_,_)=>window.AllowExit();
            if(e.Args.Length>=2&&e.Args[0]=="--ui-test"){
                window.Show();var result=await window.CheckUiAsync();
                string report=Path.GetFullPath(e.Args[1]);Directory.CreateDirectory(Path.GetDirectoryName(report)!);
                await File.WriteAllTextAsync(report,JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true}));
                window.AllowExit();Shutdown(0);return;
            }
            if(e.Args.Length>=2&&e.Args[0]=="--render-preview"){
                Directory.CreateDirectory(e.Args[1]);
                foreach(string lang in new[]{"ko","en"})foreach(double scale in new[]{1.25,1.5,2.0}){
                    window.PreviewLanguage(lang);Render(window,Path.Combine(e.Args[1],$"window-{lang}-{scale:0.00}.png"),scale);
                    if(scale==1.25){
                        var current=FrameDoubler.Core.Updates.Current;var next=new Version(current.Major,current.Minor,current.Build+1);
                        var release=new FrameDoubler.Core.UpdateRelease(next,FrameDoubler.Core.Updates.ReleasePage,
                            lang=="ko"?$"## 🚀 What's New in {next}\n\n- 업데이트의 변경 사항을 확인하세요.\n- 설치 전에 다운로드와 모든 파일의 무결성을 확인합니다.":$"## 🚀 What's New in {next}\n\n- Review changes in this update.\n- The download and every packaged file are verified before installation.",
                            $"https://github.com/deuxdoom/FrameDoubler/releases/download/v{next}/"+FrameDoubler.Core.Updates.AssetName(next),new string('0',64),207_500_000);
                        var update=new UpdateWindow(window,release,true);Render(update,Path.Combine(e.Args[1],$"updates-{lang}.png"),scale);
                        string request=Path.Combine(e.Args[1],"request.json");
                        var installing=new InstallerWindow(request,e.Args[1],lang=="en");installing.PreviewState("install");
                        Render(installing,Path.Combine(e.Args[1],$"installer-{lang}.png"),scale);
                        var rolledBack=new InstallerWindow(request,e.Args[1],lang=="en");
                        rolledBack.PreviewState("starting",new FrameDoubler.Core.InstallResult(false,"rolled-back","The new version exited before confirming startup","",next.ToString(3)));
                        Render(rolledBack,Path.Combine(e.Args[1],$"installer-rolled-back-{lang}.png"),scale);
                        var locked=new InstallerWindow(request,e.Args[1],lang=="en");
                        locked.PreviewState("backup",new FrameDoubler.Core.InstallResult(false,"failed","Access to the path 'C:\\Games\\FrameDoubler' is denied.","",next.ToString(3)));
                        Render(locked,Path.Combine(e.Args[1],$"installer-locked-{lang}.png"),scale);
                        foreach(var trigger in new[]{FrameDoubler.App.MainWindow.ExitTrigger.Close,FrameDoubler.App.MainWindow.ExitTrigger.Tray})
                            Render(window.CreateExitDialog(trigger),Path.Combine(e.Args[1],$"exit-{trigger.ToString().ToLowerInvariant()}-{lang}.png"),scale);
                        var latest=new BrandDialog(null,lang=="ko"?"업데이트":"Updates");
                        latest.Configure(BrandDialog.Glyphs.Done,lang=="ko"?"최신 버전입니다":"You're up to date",
                            lang=="ko"?$"지금 사용 중인 {FrameDoubler.Core.Updates.Current} 버전이 최신입니다.":$"FrameDoubler {FrameDoubler.Core.Updates.Current} is the latest version.",null,
                            new DialogButton(lang=="ko"?"확인":"OK",DialogChoice.Cancel,"PrimaryButton",true));
                        Render(latest,Path.Combine(e.Args[1],$"up-to-date-{lang}.png"),scale);
                        foreach(string key in new[]{"general","window","advanced","data","about"})
                            Render(new SettingsWindow(window,key,true),Path.Combine(e.Args[1],$"settings-{key}-{lang}.png"),scale);
                        using var tray=window.CreateTrayMenu();
                        tray.CreateControl();tray.Size=tray.GetPreferredSize(System.Drawing.Size.Empty);tray.PerformLayout();
                        using var trayImage=new System.Drawing.Bitmap(tray.Width,tray.Height);
                        tray.DrawToBitmap(trayImage,new System.Drawing.Rectangle(System.Drawing.Point.Empty,tray.Size));
                        trayImage.Save(Path.Combine(e.Args[1],$"tray-{lang}.png"),System.Drawing.Imaging.ImageFormat.Png);
                        tray.Items[0].Select();tray.DrawToBitmap(trayImage,new System.Drawing.Rectangle(System.Drawing.Point.Empty,tray.Size));
                        trayImage.Save(Path.Combine(e.Args[1],$"tray-hover-{lang}.png"),System.Drawing.Imaging.ImageFormat.Png);
                    }
                }
                window.Width=820;window.Height=480;
                Render(window,Path.Combine(e.Args[1],"window-small-advanced.png"),1.5);
                window.AllowExit();Shutdown(0);return;
            }
            if(e.Args.Length>=2&&e.Args[0]=="--self-test"){
                string json=Path.GetFullPath(e.Args[1]);Directory.CreateDirectory(Path.GetDirectoryName(json)!);
                await window.InitializeAsync(false);
                if(!window.HardwareReady)throw new IOException("Hardware/encoder initialization failed");
                if(e.Args.Length>2){window.SelfTestOutputFolder(Path.GetDirectoryName(json)!);if(e.Args.Contains("--verify"))window.VerifyCheck.IsChecked=true;await window.AddFilesAsync([e.Args[2]]);await window.RunQueueAsync();}
                Render(window,Path.ChangeExtension(json,".png"),1.25);
                await File.WriteAllTextAsync(json,JsonSerializer.Serialize(new{GuiCreated=true,window.English,Verification=window.VerifyCheck.IsChecked==true,Entries=window.Entries.Select(x=>new{x.Input,x.Output,x.State,x.Analysis,x.Estimate,x.Result})},new JsonSerializerOptions{WriteIndented=true}));
                window.AllowExit();Shutdown(e.Args.Length>2&&(window.Entries.Count!=1||window.Entries[0].State is not ("done" or "unverified"))?2:0);return;
            }
            if(e.Args.Length>=3&&e.Args[0]=="--update-test"){await UpdateTestAsync(window,e.Args[1],e.Args[2]);return;}
            window.Show();
            if(updated){
                // The installer rolls back unless the new version confirms that its window opened.
                await Dispatcher.InvokeAsync(()=>{},System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                try{FrameDoubler.Core.UpdateInstaller.AcknowledgeStartup(e.Args[1]);window.Log(window.English?$"Updated to {FrameDoubler.Core.Updates.Current}.":$"{FrameDoubler.Core.Updates.Current} 버전으로 업데이트했습니다.");}
                catch(Exception ex){window.Log((window.English?"Update confirmation: ":"업데이트 확인: ")+ex.Message);}
            }else _=Task.Run(()=>{try{FrameDoubler.Core.UpdateInstaller.CleanupFinished(FrameDoubler.Core.AppPaths.InstallationDirectory);}catch(Exception){}});
            await window.InitializeAsync();
            // "--updated <request>" is not a video; only plain file arguments are queued.
            if(!updated&&e.Args.Length>0)await window.AddFilesAsync(e.Args.Where(File.Exists));
        }catch(Exception ex){
            if(e.Args.Length>=2&&e.Args[0].StartsWith("--",StringComparison.Ordinal))await File.WriteAllTextAsync(e.Args[1]+".error.txt",ex.ToString());
            else MessageBox.Show(ex.Message,"FrameDoubler",MessageBoxButton.OK,MessageBoxImage.Error);
            (MainWindow as MainWindow)?.AllowExit();
            Shutdown(1);
        }
    }
    // "--update-test <report> <package.zip>": presses "Install update" for a local package in an isolated
    // test instance (FRAMEDOUBLER_INSTANCE_TEST_SCOPE must be set), then follows the real update path.
    private static async Task UpdateTestAsync(MainWindow window,string report,string package){
        if(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FRAMEDOUBLER_INSTANCE_TEST_SCOPE")))throw new InvalidOperationException("--update-test needs FRAMEDOUBLER_INSTANCE_TEST_SCOPE");
        package=Path.GetFullPath(package);
        string hash;using(var file=File.OpenRead(package))hash=Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(file));
        Version version;
        using(var zip=System.IO.Compression.ZipFile.OpenRead(package)){
            var entry=zip.Entries.Single(x=>x.FullName.Replace('\\','/').EndsWith("/manifest.json",StringComparison.Ordinal));
            using var manifest=JsonDocument.Parse(entry.Open());version=Version.Parse(manifest.RootElement.GetProperty("version").GetString()!);
        }
        window.Show();
        var release=new FrameDoubler.Core.UpdateRelease(version,FrameDoubler.Core.Updates.ReleasePage,"## Update test\n\n- Local package",
            $"https://github.com/deuxdoom/FrameDoubler/releases/download/v{version}/{FrameDoubler.Core.Updates.AssetName(version)}",hash,new FileInfo(package).Length);
        var update=new UpdateWindow(window,release){LocalPackage=package};
        update.Loaded+=async(_,_)=>{
            await update.InstallNowAsync();
            // Success closes both windows; a failure is written out instead of waiting for a click.
            if(!update.IsVisible)return;
            await File.WriteAllTextAsync(Path.GetFullPath(report),"FAILED "+update.LastError);
            window.AllowExit();update.Close();window.Close();
        };
        update.ShowDialog();
    }
    private async Task ApplyUpdateAsync(string request){
        ShutdownMode=ShutdownMode.OnExplicitShutdown;
        request=Path.GetFullPath(request);
        bool english=false;
        try{english=JsonSerializer.Deserialize<FrameDoubler.Core.InstallRequest>(await File.ReadAllTextAsync(request))?.English==true;}catch(Exception ex)when(ex is IOException or JsonException or UnauthorizedAccessException){}
        var installer=new InstallerWindow(request,FrameDoubler.Core.AppPaths.InstallationDirectory,english);
        var result=await installer.RunAsync();
        Shutdown(result.Success?0:1);
    }
    private static void Render(Window window,string path,double scale){
        if(window is MainWindow main)main.AdaptLayout(window.Height);
        var content=(FrameworkElement)window.Content;
        // Dialogs size themselves to their content (Height is NaN until shown).
        double height=window.Height;
        if(double.IsNaN(height)){content.Measure(new Size(window.Width,double.PositiveInfinity));height=Math.Ceiling(content.DesiredSize.Height);}
        content.Measure(new Size(window.Width,height));content.Arrange(new Rect(0,0,window.Width,height));content.UpdateLayout();
        var image=new RenderTargetBitmap((int)(window.Width*scale),(int)(height*scale),96*scale,96*scale,PixelFormats.Pbgra32);
        var background=new DrawingVisual();using(var draw=background.RenderOpen())draw.DrawRectangle(window.Background,null,new Rect(0,0,window.Width,height));
        image.Render(background);image.Render(content);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using var file=File.Create(path);encoder.Save(file);
    }
}
