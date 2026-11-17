using FrameDoubler.Core;
using System.Text.Json;

using var cancel=new CancellationTokenSource();
Console.CancelKeyPress+=(_,e)=>{e.Cancel=true;cancel.Cancel();};
try{
    var paths=AppPaths.Discover();var pipeline=new ConversionPipeline(paths);
    if(args.Length==0){Console.WriteLine("FrameDoubler: hardware | analyze FILE | estimate FILE [--verify] | run -i FILE [-o FILE] | verify INPUT OUTPUT | batch FOLDER\n"+
        "  --preset source|source-scaled  --height N  --accelerator auto|vulkan|rtx\n  accel status|install --accept-licenses|remove (optional runtime from official PyPI)");return 0;}
    string? Get(string name){int i=Array.IndexOf(args,name);if(i<0)return null;if(i+1>=args.Length)throw new ArgumentException("Missing value: "+name);return args[i+1];}
    bool Has(string name)=>args.Contains(name);
    var json=new JsonSerializerOptions{WriteIndented=true};
    void Print(object value)=>Console.WriteLine(JsonSerializer.Serialize(value,json));
    var progress=new Progress<ConversionProgress>(x=>{if(!Has("--quiet"))Console.Error.WriteLine($"{x.Stage} {x.Frames}/{x.Total} {x.Message}");});
    // Two fixed source presets, source by default. CLI overrides remain explicit.
    ConversionOptions Options(){
        string? key=Get("--preset");
        var preset=key is null?OutputPresets.Find("source"):OutputPresets.FindExact(key)??throw new ArgumentException("Unknown preset: "+key);
        var options=OutputPresets.Apply(preset,new ConversionOptions{Fps=Get("--fps") is { } fps?Rational.Parse(fps):null,
            Model=Get("--model")??"v4.25",Device=Get("--device")??"auto",Encoder=Get("--encoder")??"auto",
            Accelerator=Get("--accelerator")??"auto",
            Deduplicate=!Has("--no-dedup"),Verify=!Has("--no-verify"),Overwrite=Has("--overwrite"),HardwareEncoding=!Has("--software"),
            MaxFrames=Get("--max-frames") is { } max?int.Parse(max):null,
            Hud=args.Select((x,i)=>(x,i)).Where(x=>x.x=="--hud-region").Select(x=>{
                int[] values=args[x.i+1].Split(',').Select(int.Parse).ToArray();
                if(values.Length!=4)throw new ArgumentException("HUD must be X,Y,W,H");return new HudRegion(values[0],values[1],values[2],values[3]);}).ToArray()});
        if(Get("--codec") is { } codec)options=options with{Codec=codec};
        if(Get("--quality") is { } quality)options=options with{Quality=int.Parse(quality),MatchSource=false};
        if(Get("--bitrate") is { } bitrate)options=options with{Bitrate=bitrate};
        if(Get("--height") is { } height)options=options with{OutputHeight=int.Parse(height)};
        return options;
    }
    switch(args[0]){
        case "accel":{
            if(args.Length<2||args[1]=="status")Print(new{Installed=Acceleration.Installed(paths),EngineAvailable=File.Exists(Acceleration.Engine(paths)),Acceleration.DownloadBytes,Packages=Acceleration.Packages,Acceleration.LicenseTerms});
            else if(args[1]=="install"){
                if(!Has("--accept-licenses")){
                    Print(new{Acceleration.DownloadBytes,Packages=Acceleration.Packages,Acceleration.LicenseTerms});
                    Console.Error.WriteLine("출처·크기·라이선스를 확인한 뒤 accel install --accept-licenses 로 설치하세요.");return 1;
                }
                await Acceleration.InstallAsync(paths,new Progress<string>(Console.Error.WriteLine),cancel.Token);
            }
            else if(args[1]=="remove")Acceleration.Remove(paths);
            else throw new ArgumentException("Unknown accel command");break;
        }
        case "hardware":{var found=await pipeline.HardwareAsync(cancel.Token);Print(new{found.Hardware,found.Encoders});break;}
        case "analyze":Print(await pipeline.AnalyzeAsync(args[1],progress,cancel.Token));break;
        case "estimate":{var analysis=await pipeline.AnalyzeAsync(args[1],cancel:cancel.Token);Print(new{Analysis=analysis,Estimate=await pipeline.EstimateAsync(analysis,Options() with{Verify=Has("--verify")},cancel.Token)});break;}
        case "run":{
            string input=Get("-i")??throw new ArgumentException("Missing -i input");
            string output=Get("-o")??Path.Combine(Path.GetDirectoryName(Path.GetFullPath(input))!,Path.GetFileNameWithoutExtension(input)+"_smooth.mp4");
            var result=await pipeline.RunAsync(input,output,Options(),progress,cancel.Token);Print(result);if(result.Verification?.Ok==false)return 2;break;
        }
        case "verify":{
            var check=await Verifier.VerifyAsync(paths,args[1],args[2],Get("--fps") is { } f?Rational.Parse(f):null,cancel.Token);
            Print(check);return check.Ok?0:2;
        }
        case "batch":{
            string root=Path.GetFullPath(args[1]);string output=Path.GetFullPath(Get("-o")??Path.Combine(root,"FrameDoubler"));
            string[] files=Directory.GetFiles(root,"*",Has("--recursive")?SearchOption.AllDirectories:SearchOption.TopDirectoryOnly)
                .Where(x=>new[]{".mp4",".mkv",".mov",".ts"}.Contains(Path.GetExtension(x).ToLowerInvariant()))
                .Where(x=>!Path.GetFullPath(x).StartsWith(output+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            var results=new List<object>();var used=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(string input in files){
                cancel.Token.ThrowIfCancellationRequested();
                string relative=Path.ChangeExtension(Path.GetRelativePath(root,input),null)+"_smooth";
                string target=Path.Combine(output,relative+".mp4");int index=2;
                while(!used.Add(target)||File.Exists(target))target=Path.Combine(output,$"{relative}_{index++}.mp4");
                try{results.Add(await pipeline.RunAsync(input,target,Options(),progress,cancel.Token));}
                catch(OperationCanceledException){throw;}
                catch(Exception ex){results.Add(new{Input=input,Error=ex.Message});if(Has("--stop-on-error"))break;}
            }
            Print(results);break;
        }
        default:throw new ArgumentException("Unknown command");
    }
    return 0;
}catch(OperationCanceledException){Console.Error.WriteLine("Cancelled");return 130;}
catch(Exception ex){Console.Error.WriteLine(ex.Message);return 1;}
