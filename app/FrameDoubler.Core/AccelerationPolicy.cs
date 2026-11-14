namespace FrameDoubler.Core;

public sealed partial class ConversionPipeline
{
    private async Task<DeviceChoice> ChooseAccelerationAsync(ConversionOptions options,MediaInfo source,DeviceChoice device,CancellationToken cancel){
        if(options.Accelerator is not ("auto" or "vulkan" or "rtx"))throw new ArgumentException("Unknown accelerator");
        if(options.Accelerator=="vulkan"||device.Device=="cpu")return device;
        string? unavailable=null;
        if(source.BitDepth>8)unavailable="RGB16은 ncnn FP32 경로로 처리합니다.";
        else if(!Acceleration.Installed(paths))unavailable="GPU 가속 추가 기능이 설치되지 않아 ncnn으로 처리합니다.";
        else try{
            await Acceleration.VerifyInstalledAsync(paths,cancel);
            var candidates=await Acceleration.ProbeAsync(paths,cancel:cancel);
            var matching=candidates.Where(x=>x.VendorId==device.Gpu!.VendorId&&x.DeviceId==device.Gpu.DeviceId).ToArray();
            // Identical adapters require an unambiguous physical-device mapping; do not guess an index.
            if(matching.Length!=1||hardware?.Gpus.Count(x=>x.VendorId==device.Gpu!.VendorId&&x.DeviceId==device.Gpu.DeviceId)!=1)throw new IOException("장치 대응 관계를 확정할 수 없습니다.");
            if(!File.Exists(Acceleration.Model(paths,options.Model,source.Height>1440?.5:1)))throw new IOException("가속 모델이 없습니다.");
            return device with{Backend=Acceleration.Id,AccelerationIndex=matching[0].Index,RuntimeDirectory=Acceleration.Runtime(paths),Explanation=device.Gpu!.Name+" / TensorRT RTX / FP32"};
        }catch(OperationCanceledException)when(!cancel.IsCancellationRequested){unavailable="GPU 가속 확인 시간 초과로 ncnn을 사용합니다.";}
        catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or TimeoutException or System.ComponentModel.Win32Exception){unavailable="GPU 가속을 사용할 수 없어 ncnn으로 처리합니다: "+ex.Message;}
        return options.Accelerator=="rtx"||Acceleration.Installed(paths)?device with{Explanation=device.Explanation+" · "+unavailable}:device;
    }
}
