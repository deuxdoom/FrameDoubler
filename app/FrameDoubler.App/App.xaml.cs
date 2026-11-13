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
    protected override async void OnStartup(StartupEventArgs e){
        base.OnStartup(e);
        try{
            if(e.Args.Length>=2&&e.Args[0].StartsWith("--",StringComparison.Ordinal)&&string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FRAMEDOUBLER_CONFIG_DIR"))){
                string target=Path.GetFullPath(e.Args[1]);string parent=e.Args[0]=="--render-preview"?target:Path.GetDirectoryName(target)!;
                Environment.SetEnvironmentVariable("FRAMEDOUBLER_CONFIG_DIR",Path.Combine(parent,"diagnostic-settings"));
            }
            var window=new MainWindow();MainWindow=window;
            if(e.Args.Length>=2&&e.Args[0]=="--ui-test"){
                window.Show();var result=await window.CheckUiAsync();
                string report=Path.GetFullPath(e.Args[1]);Directory.CreateDirectory(Path.GetDirectoryName(report)!);
                await File.WriteAllTextAsync(report,JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true}));
                Shutdown(0);return;
            }
            if(e.Args.Length>=2&&e.Args[0]=="--render-preview"){
                Directory.CreateDirectory(e.Args[1]);
                foreach(string lang in new[]{"ko","en"})foreach(double scale in new[]{1.25,1.5,2.0}){
                    window.PreviewLanguage(lang);Render(window,Path.Combine(e.Args[1],$"window-{lang}-{scale:0.00}.png"),scale);
                    if(scale==1.25){
                        var release=new FrameDoubler.Core.UpdateRelease(new Version(2,1,0),FrameDoubler.Core.Updates.ReleasePage,
                            lang=="ko"?"새 네이티브 패키지의 변경 사항을 확인하세요.\n설치 전에 다운로드와 모든 파일의 무결성을 확인합니다.":"Review changes in the new native package.\nThe download and every packaged file are verified before installation.",null,null,0);
                        var update=new UpdateWindow(window,release,true);Render(update,Path.Combine(e.Args[1],$"updates-{lang}.png"),scale);
                    }
                }
                window.Width=820;window.Height=480;window.Advanced.IsExpanded=true;
                Render(window,Path.Combine(e.Args[1],"window-small-advanced.png"),1.5);
                Shutdown(0);return;
            }
            if(e.Args.Length>=2&&e.Args[0]=="--self-test"){
                string json=Path.GetFullPath(e.Args[1]);Directory.CreateDirectory(Path.GetDirectoryName(json)!);
                await window.InitializeAsync(false);
                if(!window.HardwareReady)throw new IOException("Hardware/encoder initialization failed");
                if(e.Args.Length>2){window.SelfTestOutputFolder(Path.GetDirectoryName(json)!);await window.AddFilesAsync([e.Args[2]]);await window.RunQueueAsync();
                    if(window.Entries.Count!=1||window.Entries[0].State!="done")throw new IOException("GUI conversion/verification failed");}
                Render(window,Path.ChangeExtension(json,".png"),1.25);
                await File.WriteAllTextAsync(json,JsonSerializer.Serialize(new{GuiCreated=true,window.English,Entries=window.Entries.Select(x=>new{x.Input,x.Output,x.State})},new JsonSerializerOptions{WriteIndented=true}));
                Shutdown(0);return;
            }
            window.Show();await window.InitializeAsync();
            if(e.Args.Length>0)await window.AddFilesAsync(e.Args.Where(File.Exists));
        }catch(Exception ex){
            if(e.Args.Length>=2&&e.Args[0].StartsWith("--",StringComparison.Ordinal))await File.WriteAllTextAsync(e.Args[1]+".error.txt",ex.ToString());
            else MessageBox.Show(ex.Message,"FrameDoubler",MessageBoxButton.OK,MessageBoxImage.Error);
            Shutdown(1);
        }
    }
    private static void Render(Window window,string path,double scale){
        if(window is MainWindow main)main.AdaptLayout(window.Height);
        var content=(FrameworkElement)window.Content;
        content.Measure(new Size(window.Width,window.Height));content.Arrange(new Rect(0,0,window.Width,window.Height));content.UpdateLayout();
        var image=new RenderTargetBitmap((int)(window.Width*scale),(int)(window.Height*scale),96*scale,96*scale,PixelFormats.Pbgra32);
        var background=new DrawingVisual();using(var draw=background.RenderOpen())draw.DrawRectangle(window.Background,null,new Rect(0,0,window.Width,window.Height));
        image.Render(background);image.Render(content);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using var file=File.Create(path);encoder.Save(file);
    }
}
