using FrameDoubler.Core;

// Capture preview: settings round trip, option commands, status parsing, device choices and
// (when the engine is built) the native engine's own synthetic checks.
internal static class LiveTests
{
    public static void Run(Action<bool,string> assert){
        string? previous=Environment.GetEnvironmentVariable("FRAMEDOUBLER_CONFIG_DIR");
        string root=Path.Combine(EncoderDetector.WorkRoot,"live-tests",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try{
            Environment.SetEnvironmentVariable("FRAMEDOUBLER_CONFIG_DIR",root);
            var saved=new Preferences{StartPage="convert",CaptureDevice="Live Gamer \"Ultra\" 2.1-Video",CaptureDeviceId="device-path-2",CaptureMode="3840x2160@60/1 NV12",CaptureAudio="{0.0.1}.{abc}",CaptureAudioOutput="{0.0.0}.{render}",
                LiveFrameGeneration="vfx",LiveMultiplier=2,LiveAntiAlias=3,LiveAntiAliasMode="smaa",LiveTransfer="hlg",LiveDedup=false,LiveRange="full",LiveMatrix="2020",LiveVfxQuality=2,LiveVolume=35,LiveMute=true,
                LiveAudioOffset=-40,LiveStats=false,LiveSync="tearing",LiveAspect="fill",LiveOutputCap="144",LiveHoldOriginal=false,LiveDlssMotion="nvof",LiveEdgeAegis=true,
                ScreenshotDirectory="F:\\캡처 사진; #1",RecordingDirectory="F:\\녹화 = 영상",RecordingCodec="hevc",RecordingBitrate=80,
                ShortcutScreenshot="Ctrl+Shift+S",ShortcutRecord="F9",ShortcutFullscreen="",ShortcutFrameGeneration="Alt+G",ShortcutStats="F12",ShortcutOriginal="B",GlobalShortcuts=true};
            // Optional NVIDIA VFX download: only listed libraries and license files leave the archive, and every hash is checked.
            {
                string vfx=Path.Combine(root,"vfx");Directory.CreateDirectory(vfx);string zip=Path.Combine(vfx,"wheel.zip");
                byte[] good=System.Text.Encoding.ASCII.GetBytes("runtime-library"),other=System.Text.Encoding.ASCII.GetBytes("unrelated");
                using(var archive=System.IO.Compression.ZipFile.Open(zip,System.IO.Compression.ZipArchiveMode.Create)){
                    void Put(string name,byte[] data){using var stream=archive.CreateEntry(name).Open();stream.Write(data);}
                    Put("nvvfx/libs/Needed.dll",good);Put("nvvfx/libs/Unused.dll",other);Put("nvvfx/other/Needed.dll",other);
                    Put("nvidia_vfx-0.2.0.0.dist-info/licenses/packaging/LICENSE.txt",other);
                }
                string hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(good)).ToLowerInvariant();
                string output=Path.Combine(vfx,"out");
                VfxRuntime.ExtractAsync(zip,output,[new VfxFile("Needed.dll",good.Length,hash)]).GetAwaiter().GetResult();
                assert(File.Exists(Path.Combine(output,"Needed.dll"))&&!File.Exists(Path.Combine(output,"Unused.dll"))&&File.Exists(Path.Combine(output,"licenses","LICENSE.txt")),
                    "NVIDIA VFX extraction keeps the listed library and license files only");
                bool rejected=false;
                try{VfxRuntime.ExtractAsync(zip,Path.Combine(vfx,"bad"),[new VfxFile("Needed.dll",good.Length,new string('0',64))]).GetAwaiter().GetResult();}catch(IOException){rejected=true;}
                bool missing=false;
                try{VfxRuntime.ExtractAsync(zip,Path.Combine(vfx,"missing"),[new VfxFile("Absent.dll",1,hash)]).GetAwaiter().GetResult();}catch(IOException){missing=true;}
                assert(rejected&&missing,"NVIDIA VFX extraction rejects a wrong hash and a missing library");
                string vfxEngine=Path.Combine(vfx,"app","engine");
                // DownloadDirectory returns a full path; the work root may be given with '/' separators.
                assert(VfxRuntime.DownloadDirectory(vfxEngine)==Path.GetFullPath(Path.Combine(vfx,"app","accel","nvvfx"))&&!VfxRuntime.Downloaded(vfxEngine)&&VfxRuntime.Files.Length==5&&VfxRuntime.InstalledBytes==210788400,
                    "NVIDIA VFX installs beside the other optional add-on (kept across updates) and is absent until downloaded");
                // With NVIDIA's wheel already on this development PC, run the real install path (hash, extraction, load) without the network.
                string? repo=AppContext.BaseDirectory;
                while(repo is not null&&!File.Exists(Path.Combine(repo,"tools","dev","nvvfx","download","nvidia_vfx-0.2.0.0-cp312-abi3-win_amd64.whl")))repo=Path.GetDirectoryName(repo);
                if(repo is not null){
                    string wheel=Path.Combine(repo,"tools","dev","nvvfx","download","nvidia_vfx-0.2.0.0-cp312-abi3-win_amd64.whl");
                    Directory.CreateDirectory(vfxEngine);
                    VfxRuntime.InstallAsync(vfxEngine,null,default,(target,_)=>{File.Copy(wheel,target);return Task.CompletedTask;}).GetAwaiter().GetResult();
                    string installed=VfxRuntime.DownloadDirectory(vfxEngine);
                    assert(VfxRuntime.Downloaded(vfxEngine)&&Directory.GetFiles(Path.Combine(installed,"licenses")).Length>=3&&!File.Exists(Path.Combine(installed,"nvidia-vfx.whl")),
                        "NVIDIA VFX install from the official wheel: five verified libraries, license files, no leftover archive");
                    VfxRuntime.Remove(vfxEngine);
                    assert(!VfxRuntime.Downloaded(vfxEngine)&&!Directory.Exists(installed),"NVIDIA VFX removal deletes the downloaded runtime");
                    // Loaded libraries stay locked by this process, so removal is checked first and the runtime is installed again for the load check.
                    VfxRuntime.InstallAsync(vfxEngine,null,default,(target,_)=>{File.Copy(wheel,target);return Task.CompletedTask;}).GetAwaiter().GetResult();
                    if(Environment.GetEnvironmentVariable("FRAMEDOUBLER_LIVE_DIR") is not {Length:>0}&&File.Exists(Path.Combine(repo,"build","live","fdlive.dll")))
                        Environment.SetEnvironmentVariable("FRAMEDOUBLER_LIVE_DIR",Path.Combine(repo,"build","live"));
                    if(LiveEngine.Available){
                        using var vfxResult=System.Text.Json.JsonDocument.Parse(LiveEngine.SelfTest(installed));
                        bool vfxLoaded=vfxResult.RootElement.GetProperty("checks").EnumerateArray().Any(x=>x.GetProperty("name").GetString()=="vfx"&&x.GetProperty("ok").GetBoolean());
                        assert(vfxLoaded,"The downloaded NVIDIA VFX runtime loads and generates a frame in the live engine");
                    }
                }
            }
            saved.Save();
            var loaded=Preferences.Load();
            assert(loaded with{}==saved,"Every capture preview setting survives a restart, including quotes, ';', '#', '=' and Korean paths");
            File.WriteAllText(Path.Combine(root,Preferences.FileName),"[FrameDoubler]\nStartPage=\"x\"\nLiveFrameGeneration=\"xess\"\nLiveMultiplier=\"9\"\nLiveAntiAlias=\"-1\"\nLiveRange=\"x\"\nLiveSync=\"fast\"\nLiveAspect=\"zoom\"\nLiveOutputCap=\"5\"\nLiveVolume=\"500\"\nRecordingCodec=\"av1\"\nLiveAntiAliasMode=\"fxaa\"\nLiveTransfer=\"dolby\"\nLiveMatrix=\"2100\"\n");
            var fixedUp=Preferences.Load();
            assert(fixedUp.StartPage=="capture"&&fixedUp.LiveFrameGeneration=="off"&&fixedUp.LiveMultiplier==4&&fixedUp.LiveAntiAlias==0&&fixedUp.LiveRange=="auto"&&
                   fixedUp.LiveSync=="auto"&&fixedUp.LiveAspect=="fit"&&fixedUp.LiveOutputCap=="off"&&fixedUp.LiveVolume==100&&fixedUp.RecordingCodec=="h264"&&
                   fixedUp.LiveAntiAliasMode=="edge"&&fixedUp.LiveTransfer=="auto"&&fixedUp.LiveMatrix=="auto",
                "Unknown or out-of-range capture settings fall back to safe values");
            foreach(int multiplier in new[]{2,3,4}){
                new Preferences{LiveMultiplier=multiplier}.Save();
                assert(Preferences.Load().LiveMultiplier==multiplier,"Content multiplier survives restart: "+multiplier);
            }
            File.WriteAllText(Path.Combine(root,Preferences.FileName),"[FrameDoubler]\nLiveFrameGeneration=\"own\"\n");
            assert(Preferences.Load().LiveFrameGeneration=="auto","A saved FrameDoubler-interpolation choice becomes Automatic: live capture uses DLSS or VFX only");
            File.WriteAllText(Path.Combine(root,Preferences.FileName),"[FrameDoubler]\nLiveMultiplier=\"1\"\n");
            assert(Preferences.Load().LiveMultiplier==2&&new LiveOptions().Multiplier==2,"Legacy signal multiplier migrates to the default content 2x");
            File.WriteAllText(Path.Combine(root,Preferences.FileName),"[FrameDoubler]\n");
            assert(Preferences.Load().LiveFrameGeneration=="off"&&!Preferences.Load().LiveDedup&&new LiveOptions().FrameGeneration=="off"&&!new LiveOptions().Dedup,
                "Fresh and missing capture settings disable frame generation and deduplication");
            assert(Preferences.Load().LiveDlssMotion=="none"&&!Preferences.Load().LiveEdgeAegis&&new LiveOptions().DlssMotion=="none"&&!new LiveOptions().EdgeAegis,
                "NVOF and Edge Aegis are opt-in for new and existing settings");
            foreach(string motion in new[]{"none","own","auto","unknown"}){
                new Preferences{LiveDlssMotion=motion,LiveEdgeAegis=true}.Save();
                var normal=Preferences.Load();
                assert(normal.LiveDlssMotion=="none"&&!normal.LiveEdgeAegis,"Edge Aegis needs an explicit NVOF choice: "+motion);
            }
            assert(new Preferences().EffectiveScreenshotDirectory.EndsWith("FrameDoubler",StringComparison.Ordinal)&&new Preferences().EffectiveRecordingDirectory!=new Preferences().EffectiveScreenshotDirectory,
                "Default screenshot and recording folders are separate FrameDoubler folders");
        }finally{
            Environment.SetEnvironmentVariable("FRAMEDOUBLER_CONFIG_DIR",previous);
            try{Directory.Delete(root,true);}catch(IOException){}catch(UnauthorizedAccessException){} // loaded NVIDIA libraries stay locked until exit
        }

        var options=new LiveOptions(FrameGeneration:"auto",Multiplier:2,AntiAlias:3,Dedup:false,Sync:"vsync",Aspect:"stretch",Compare:"split",OutputCap:"display");
        assert(new LiveOptions(FrameGeneration:"auto").Commands(true,true).Contains("fg=dlss")&&new LiveOptions(FrameGeneration:"auto").Commands(true,false).Contains("fg=vfx")
            &&new LiveOptions(FrameGeneration:"dlss").Commands(false,false).Contains("fg=dlss")&&new LiveOptions(FrameGeneration:"vfx").Commands(true,true).Contains("fg=vfx")
            &&new LiveOptions(FrameGeneration:"off").Commands(true,true).Contains("fg=off"),
            "Automatic frame generation prefers DLSS, then VFX; explicit choices are kept");
        string commands=options.Commands(vfxInstalled:true);
        assert(commands.Contains("fg=vfx")&&!options.Commands(false).Contains("fg=own")&&!new LiveOptions(FrameGeneration:"own").Commands(true,true).Contains("fg=own")
               &&new LiveOptions(FrameGeneration:"off").Commands(true).Contains("fg=off"),
            "Live capture never asks the engine for FrameDoubler interpolation (an old \"own\" choice runs as Automatic)");
        foreach(string expected in new[]{"multiplier=2","aa=3","dedup=0","sync=vsync","aspect=stretch","compare=split","outputCap=display"})
            assert(commands.Split('\n').Contains(expected),"Engine command carries "+expected);
        assert(new LiveOptions(DlssMotion:"nvof",EdgeAegis:true).Commands(true,true).Split('\n').Contains("edgeAegis=1")&&
            new LiveOptions(DlssMotion:"nvof").Commands(true,true).Split('\n').Contains("dlssMotion=nvof")&&commands.Split('\n').Contains("dlssMotion=none")&&commands.Split('\n').Contains("edgeAegis=0"),
            "DLSS motion options reach the engine; the default costs no external motion work");
        assert(new LiveOptions(AntiAlias:2,AntiAliasMode:"smaa",Transfer:"pq",Matrix:"2020").Commands(true,true).Split('\n') is var hdr&&hdr.Contains("aaMode=smaa")&&hdr.Contains("transfer=pq")&&hdr.Contains("matrix=2020")&&
            commands.Split('\n').Contains("aaMode=edge")&&commands.Split('\n').Contains("transfer=auto")&&
            new LiveOptions(AntiAliasMode:"smaa\nfg=own",Transfer:"pq\nfg=own").Commands(true,true).Split('\n') is var safe&&safe.Contains("aaMode=edge")&&safe.Contains("transfer=auto")&&!safe.Contains("fg=own"),
            "SMAA and the HDR transfer reach the engine; unknown values fall back to edge tracing and automatic, never injecting commands");
        assert(new LiveOptions(DlssMotion:"nvof\nfg=own",EdgeAegis:true).Commands(true,true).Split('\n').Contains("dlssMotion=none")&&
            new LiveOptions(DlssMotion:"none",EdgeAegis:true).Commands(true,true).Split('\n').Contains("edgeAegis=0"),
            "Unknown motion choices cannot inject commands or enable refinement without NVOF");

        var status=LiveStatus.Parse("{\"running\":true,\"captureFailed\":false,\"width\":3840,\"height\":2160,\"format\":\"NV12\",\"modeFps\":60.000,\"gpu\":\"GPU \\\"X\\\"\","+
            "\"inputFps\":59.95,\"contentFps\":29.5,\"outputFps\":59.9,\"interpolatedFps\":30.4,\"presentedFps\":59.9,\"displayHz\":144,\"tearing\":true,\"skipped\":0,\"dedupMs\":0.35,\"latencyMs\":51.6,"+
            "\"stages\":{\"upload\":0.68,\"motion\":null,\"interpolate\":2.5,\"present\":0.04,\"encode\":null},\"fg\":\"vfx\",\"vfxRuntime\":\"1.3.0.0\",\"audioDelayMs\":36,\"audio\":true,"+
            "\"totals\":{\"input\":648,\"unique\":324,\"duplicates\":324,\"dropped\":0},\"recording\":{\"active\":true,\"path\":\"F:\\\\녹화\\\\a.mp4\",\"seconds\":8.5,\"dropped\":0},"+
            "\"events\":[{\"kind\":\"screenshot\",\"message\":\"saved F:\\\\a.png\"}]}");
        assert(status.Running&&status.Width==3840&&status.DisplayHz==144&&status.Tearing&&status.Gpu=="GPU \"X\""&&status.LatencyMs==51.6&&status.Stages.Motion is null&&
               Math.Abs(status.Stages.Total-3.22)<1e-9&&status.Recording&&status.RecordingPath=="F:\\녹화\\a.mp4"&&status.Duplicates==324&&status.Events.Single().Kind=="screenshot",
            "Engine status JSON is read with missing stage times kept as unknown, not zero");
        assert(status.CaptureState==""&&status.AudioOutputState==""&&status.AudioOutputAttempts==0&&new Preferences().CaptureAudioOutput=="default",
            "Older engines have unknown endpoint health; new installations follow the system output");
        foreach(double rate in new[]{0.0,29.97,60.0}){
            var shown=LiveStatus.Parse("{\"distinctFps\":"+rate.ToString(System.Globalization.CultureInfo.InvariantCulture)+",\"presentedFps\":144,\"stages\":{},\"totals\":{},\"recording\":{}}");
            assert(shown.ShownFps==rate,"The display rate preserves changed-picture counts, including zero: "+rate);
        }
        assert(status.DistinctFps is null&&status.ShownFps==status.PresentedFps,"Only older engines without distinctFps use the Present rate");
        var saving=LiveStatus.Parse("{\"running\":true,\"stages\":{},\"totals\":{},\"recording\":{\"active\":false,\"finalizing\":true,\"fps\":120,\"path\":\"record.mp4\"}}");
        assert(!saving.Recording&&saving.RecordingFinalizing&&saving.RecordingFps==120&&saving.RecordingPath=="record.mp4",
            "Saving is distinct from active recording and retains the declared video rate");
        var hdrStatus=LiveStatus.Parse("{\"running\":true,\"format\":\"P010\",\"colorMatrix\":\"2020\",\"colorMatrixSource\":\"default\",\"colorTransfer\":\"pq\",\"colorTransferSource\":\"default\","+
            "\"aaMethod\":\"edge\",\"aaWarning\":\"SMAA shader EdgePS: failed\",\"stages\":{},\"totals\":{},\"recording\":{}}");
        assert(hdrStatus.ColorTransfer=="pq"&&hdrStatus.ColorTransferSource=="default"&&hdrStatus.ColorMatrix=="2020"&&hdrStatus.AaMethod=="edge"&&hdrStatus.AaWarning.StartsWith("SMAA",StringComparison.Ordinal)&&
               status.ColorTransfer==""&&status.AaMethod=="",
            "HDR transfer, BT.2020 and the anti-aliasing actually used are read; older engines leave them unknown");
        var starting=LiveStatus.Parse("{\"running\":true,\"stages\":{},\"totals\":{},\"recording\":{\"starting\":true}}");
        assert(starting.RecordingStarting&&!starting.Recording&&!starting.RecordingFinalizing,"A pending start can be cancelled before an active recording exists");
        var limited=LiveStatus.Parse("{\"dlssMaxMultiplier\":2,\"fg\":\"vfx\",\"multiplier\":3,\"stages\":{},\"totals\":{},\"recording\":{}}");
        assert(limited.DlssMaxMultiplier==2&&limited.FrameGeneration=="vfx"&&limited.Multiplier==3&&starting.DlssMaxMultiplier==0&&starting.Multiplier==0,
            "DLSS reports the driver multiplier limit alongside the backend and multiplier actually run; absent values are unknown");
        assert(LiveStatus.Parse("{\"fg\":\"dlss\",\"dlssMotion\":\"nvof\",\"stages\":{},\"totals\":{},\"recording\":{}}").DlssMotion=="nvof"&&starting.DlssMotion=="",
            "The reported DLSS motion source is explicit; an older engine does not imply hardware optical flow");
        var motionStatus=LiveStatus.Parse("{\"edgeAegis\":true,\"motionWarning\":\"driver unavailable\",\"stages\":{},\"totals\":{},\"recording\":{}}");
        assert(motionStatus.EdgeAegis&&motionStatus.MotionWarning=="driver unavailable"&&!starting.EdgeAegis&&starting.MotionWarning=="",
            "Refinement and external-motion fallback are explicit; older engines report neither as active");

        var modes=new List<CaptureMode>{new(3840,2160,30,1,"NV12",true),new(3840,2160,60,1,"YUY2",true),new(3840,2160,60,1,"NV12",true),new(1920,1080,240,1,"NV12",true),
            new(3840,2160,60,1,"P010",false),new(3840,2160,60000,1001,"NV12",true)};
        var device=new CaptureDevice("Live Gamer Ultra 2.1-Video","id","",modes);
        assert(device.PreferredMode==modes[2],"Default mode: largest picture at 60 fps, NV12 before YUY2, never an unsupported format");
        var inventory=new CaptureInventory([device],[new("mic","USB Microphone",true),new("card","HDMI/Line In(Live Gamer Ultra 2.1-Audio)",false)]);
        {
            string masked=Privacy.Mask(@"C:\Users\Tester\Videos tester log; Testers stays; \\?\usb#vid_07ca&pid_2553&mi_00#6&1162f4de&0&0000#{e5323777-f976-4f5b-9b55-b94699c46e44}\global","Tester",@"C:\Users\Tester");
            assert(!masked.Contains("1162f4de")&&!masked.Contains("e5323777")&&masked.Contains("vid_07ca&pid_2553")&&masked.Contains(@"%USERPROFILE%\Videos")&&masked.Contains("<user> log")&&masked.Contains("Testers"),
                "Diagnostics hide the profile path, user name and device instance IDs but keep vendor/product numbers and other words: "+masked);
        }
        assert(inventory.MatchingAudio(device)?.Id=="card","The capture card's own audio input is matched by name, not the default microphone");
        {
            var second=device with{Id="second-card"};
            var duplicateCards=new CaptureInventory([device,second],inventory.Audio);
            assert(duplicateCards.PreferredDevice(second.Id,device.Name)==second&&duplicateCards.PreferredDevice("",device.Name)==device,
                "A saved device path survives reordered identical names; old name-only settings still load");
            assert(duplicateCards.MatchingAudio(device)is null&&new CaptureInventory([device],[new("a",device.Name,false),new("b",device.Name,false)]).MatchingAudio(device)is null,
                "Ambiguous cards or audio endpoints require an explicit audio choice");
            var mf=device with{Id=@"\\?\usb#instance-a#{mf-class}\global"};
            var ds=device with{Id=@"dshow:\\?\usb#instance-a#{dshow-class}\global"};
            assert(new CaptureInventory([mf,ds],inventory.Audio).MatchingAudio(mf)?.Id=="card",
                "Two APIs for one PnP instance share the audio match despite different interface-class GUIDs");
        }
        {
            var mic=new AudioInput("mic","Microphone (USB Audio Device)",true);
            CaptureDevice Card(string name)=>new(name,"id-"+name,"",[]);
            var others=new CaptureInventory([],[mic,new AudioInput("elgato","Digital Audio Interface (Game Capture HD60 S+)",false),
                new AudioInput("magewell","USB Capture HDMI 4K+ (Magewell Audio)",false),new AudioInput("stick","Digital Audio Interface (USB Digital Audio)",false)]);
            assert(others.MatchingAudio(Card("Game Capture HD60 S+"))?.Id=="elgato"&&others.MatchingAudio(Card("Elgato HD60 S+ Video"))?.Id=="elgato"
                &&others.MatchingAudio(Card("Magewell USB Capture HDMI 4K+"))?.Id=="magewell"&&others.MatchingAudio(Card("USB Video"))is null&&others.MatchingAudio(Card("HD Pro Webcam C920"))is null,
                "Other cards' audio inputs are paired by their distinctive name words; generic words (USB, HDMI, Video) never pick a microphone or another card");
        }

        // The native engine checks itself (dedup, timelines, frame generation, AA, NV12) when built.
        string engine=Environment.GetEnvironmentVariable("FRAMEDOUBLER_LIVE_DIR") is {Length:>0} dir?dir:Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..","..","..","..","build","live"));
        if(File.Exists(Path.Combine(engine,"fdlive.dll"))){
            Environment.SetEnvironmentVariable("FRAMEDOUBLER_LIVE_DIR",engine);
            string json=LiveEngine.SelfTest();
            using var document=System.Text.Json.JsonDocument.Parse(json);
            int failed=document.RootElement.GetProperty("failed").GetInt32(),passedChecks=document.RootElement.GetProperty("passed").GetInt32();
            assert(failed==0&&passedChecks>=9,"Native live engine self-test: "+passedChecks+" passed, "+failed+" failed");
        }
    }
}
