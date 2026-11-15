using System;
using System.Threading.Tasks;
using System.Windows;
using FrameDoubler.Core;

namespace FrameDoubler.App;

// NVIDIA VFX is not part of the package. Choosing it (in the capture view or in Settings) shows
// NVIDIA's license terms first and downloads only after the user accepts; the download runs in a
// dialog with progress and Cancel.
internal static class VfxDownload
{
    // True when the runtime is installed afterwards (also when it already was).
    public static bool Run(Window? owner,Func<string,string,string> T){
        if(LiveEngine.VfxInstalled)return true;
        string terms=T($"NVIDIA 공식 패키지 저장소(pypi.nvidia.com)에서 NVIDIA VFX 런타임 {VfxRuntime.DownloadBytes/1_000_000d:0}MB를 받고, 프레임 생성에 필요한 파일 {VfxRuntime.InstalledBytes/1_000_000d:0}MB만 남깁니다.\n\n이 런타임은 NVIDIA의 독점 소프트웨어이며 NVIDIA Software License Agreement와 AI 제품별 조건이 적용됩니다. 라이선스 문서는 설치 폴더의 licenses에 함께 저장합니다. NVIDIA RTX GPU와 최신 드라이버가 필요합니다.\n\n파일의 SHA-256을 확인한 뒤 설치합니다. NVIDIA 라이선스에 동의하고 받을까요?",
            $"Download the NVIDIA VFX runtime ({VfxRuntime.DownloadBytes/1_000_000d:0} MB) from NVIDIA's official package index (pypi.nvidia.com); only the {VfxRuntime.InstalledBytes/1_000_000d:0} MB needed for frame generation is kept.\n\nThe runtime is NVIDIA proprietary software under the NVIDIA Software License Agreement and the product-specific terms for AI products. The license documents are saved in the licenses folder next to it. An NVIDIA RTX GPU and a current driver are required.\n\nSHA-256 is checked before installing. Accept NVIDIA's license and download?");
        var consent=new BrandDialog(owner,"NVIDIA VFX",520);
        consent.Configure(BrandDialog.Glyphs.Update,T("NVIDIA VFX 런타임 받기","Download NVIDIA VFX runtime"),terms,null,
            new DialogButton(T("취소","Cancel"),DialogChoice.Cancel,"TextButton"),new DialogButton(T("동의하고 받기","Accept and download"),DialogChoice.Primary,"PrimaryButton",true));
        consent.ShowDialog();
        if(consent.Choice!=DialogChoice.Primary)return false;

        var dialog=new BrandDialog(owner,"NVIDIA VFX",520);
        var cancel=new DialogButton(T("취소","Cancel"),DialogChoice.Cancel,"TextButton");
        string sizes=$"{VfxRuntime.DownloadBytes/1_000_000d:0} MB";
        dialog.Configure(BrandDialog.Glyphs.Update,T("NVIDIA VFX 받는 중… 0%","Downloading NVIDIA VFX… 0%"),sizes,null,cancel);
        bool installed=false;string shown="";
        dialog.Loaded+=async(_,_)=>{
            // Closing the dialog (Cancel, Esc, the close button) cancels the download.
            var progress=new Progress<(double Fraction,string Stage)>(x=>{
                if(dialog.ClosedToken.IsCancellationRequested)return;
                string heading=x.Stage=="download"?T($"NVIDIA VFX 받는 중… {x.Fraction/.9*100:0}%",$"Downloading NVIDIA VFX… {x.Fraction/.9*100:0}%"):T("파일 확인 중…","Verifying files…");
                if(heading==shown)return;
                shown=heading;dialog.Configure(BrandDialog.Glyphs.Update,heading,sizes,null,cancel);
            });
            try{
                string engine=LiveEngine.Directory;
                await Task.Run(()=>VfxRuntime.InstallAsync(engine,progress,dialog.ClosedToken));
                installed=true;dialog.Close();
            }catch(OperationCanceledException){}
            catch(Exception ex){
                if(!dialog.ClosedToken.IsCancellationRequested)
                    dialog.Configure(BrandDialog.Glyphs.Error,T("NVIDIA VFX를 받지 못했습니다","NVIDIA VFX could not be downloaded"),ex.Message,null,
                        new DialogButton(T("닫기","Close"),DialogChoice.Cancel,"PrimaryButton",true));
            }
        };
        dialog.ShowDialog();
        return installed&&LiveEngine.VfxInstalled;
    }
}
