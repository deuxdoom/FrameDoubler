using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using FrameDoubler.Core;

namespace FrameDoubler.App;

public partial class MainWindow
{
    private CancellationTokenSource? estimateCancel;
    private Task? estimateTask;
    private readonly SemaphoreSlim estimateGate=new(1,1);
    private bool measuring;
    private string? estimateError;
    private ConversionOptions CurrentOptions()=>new(){Youtube=Selected(PresetBox)=="youtube",Fps=Selected(FpsBox)=="auto"?null:Rational.Parse(Selected(FpsBox)),
        Codec=Selected(CodecBox)=="auto"?null:Selected(CodecBox),Device=Selected(DeviceBox),Encoder=Selected(EncoderBox),Model=Selected(ModelBox),
        Quality=(int)QualitySlider.Value,Bitrate=string.IsNullOrWhiteSpace(BitrateBox.Text)?null:BitrateBox.Text.Trim(),
        HardwareEncoding=HardwareCheck.IsChecked==true,Verify=VerifyCheck.IsChecked==true,Hud=ParseHud()};
    private void EstimateAgainClicked(object sender,RoutedEventArgs e)=>ScheduleEstimate(true);
    private void ScheduleEstimate(bool refresh=false){
        if(updating||busy||!ready)return;
        estimateCancel?.Cancel();var pending=Entries.Where(x=>x.Analysis is not null&&x.Output is null).ToArray();
        foreach(var entry in pending)entry.Estimate=null;
        estimateError=null;if(pending.Length==0){RefreshEstimateText();return;}
        var own=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);estimateCancel=own;measuring=true;UpdateControls();
        estimateTask=CalculateEstimatesAsync(pending,own,refresh);
    }
    private async Task CalculateEstimatesAsync(QueueEntry[] entries,CancellationTokenSource own,bool refresh){
        bool locked=false;
        try{
            await Task.Delay(600,own.Token);await estimateGate.WaitAsync(own.Token);locked=true;
            var options=CurrentOptions();
            foreach(var entry in entries){
                var estimate=await Task.Run(()=>pipeline.EstimateAsync(entry.Analysis!,options,own.Token,refresh),own.Token);
                own.Token.ThrowIfCancellationRequested();if(Entries.Contains(entry)){entry.Estimate=estimate;entry.Refresh();RefreshEstimateText();}
            }
        }catch(OperationCanceledException){if(ReferenceEquals(estimateCancel,own))estimateError=T("측정 취소됨 · 다시 계산할 수 있습니다.","Measurement cancelled. You can recalculate.");}
        catch(Exception ex){if(ReferenceEquals(estimateCancel,own)){estimateError=ex.Message;Log(T("예상 시간: ","Time estimate: ")+ex.Message);}}
        finally{
            if(locked)estimateGate.Release();
            if(ReferenceEquals(estimateCancel,own)){estimateCancel=null;measuring=false;UpdateControls();if(closing&&!busy)Close();}
            own.Dispose();
        }
    }
    private void RefreshEstimateText(){
        if(!IsInitialized)return;
        var selected=QueueList.SelectedItem as QueueEntry;var estimate=selected?.Estimate;
        EstimateValue.Text=estimate is not null?TimeEstimates.Range(estimate,English):measuring?T("짧은 구간 측정 중…","Measuring short samples…"):"—";
        EstimateHint.Text=estimate is not null?
            (estimate.FromHistory?T("최근 실제 작업으로 보정 · ","Adjusted from recent jobs · "):T("현재 장치 실측 · ","Measured on this device · "))+
            (estimate.Verification?T("검증 포함","Includes verification"):T("검증 제외","Excludes verification")):
            estimateError??T("영상을 추가하면 예상 시간을 안내합니다.","Add a video to see an estimate.");
        EstimateValue.ToolTip=estimate is null?null:estimate.Device+" / "+estimate.Encoder;
        EstimateHint.ToolTip=T("장면, 저장 장치, 다른 앱의 부하와 발열에 따라 달라질 수 있습니다.","Content, storage, other apps and thermal load can change the actual time.");
        EstimateAgain.IsEnabled=!busy&&!measuring&&ready&&Entries.Any(x=>x.Analysis is not null&&x.Output is null);
        if(!busy){var jobs=Entries.Where(x=>x.Output is null&&x.Analysis is not null).ToArray();
            if(jobs.Length>0&&jobs.All(x=>x.Estimate is not null))RateText.Text=T("대기열 합계 · ","Queue total · ")+TimeEstimates.Duration(jobs.Sum(x=>x.Estimate!.LowerSeconds),English)+" ~ "+TimeEstimates.Duration(jobs.Sum(x=>x.Estimate!.UpperSeconds),English);
            else if(measuring)RateText.Text=T("선택한 설정으로 처리 속도를 확인하고 있습니다.","Checking processing speed with your settings.");
        }
    }
}
