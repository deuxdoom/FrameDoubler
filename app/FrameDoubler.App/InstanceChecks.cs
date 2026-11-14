using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using FrameDoubler.Core;

namespace FrameDoubler.App;

public partial class MainWindow
{
    private async Task CheckSingleInstanceAsync(Action<bool,string> check){
        string scope=Guid.NewGuid().ToString("N");
        async Task<int> Duplicate(){
            string executable=Environment.ProcessPath??throw new InvalidOperationException("No process executable");
            string[] arguments=System.IO.Path.GetFileNameWithoutExtension(executable).Equals("dotnet",StringComparison.OrdinalIgnoreCase)?[Assembly.GetEntryAssembly()!.Location]:[];
            var start=ProcessRunner.StartInfo(executable,arguments);start.Environment["FRAMEDOUBLER_INSTANCE_TEST_SCOPE"]=scope;
            using var process=ProcessRunner.Start(start,"single-instance test duplicate");
            var output=process.StandardOutput.ReadToEndAsync();var errors=process.StandardError.ReadToEndAsync();
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try{await process.WaitForExitAsync(timeout.Token);await output;await errors;return process.ExitCode;}
            finally{if(!process.HasExited)process.Kill(true);}
        }
        using(var primary=new SingleInstance(scope)){
            check(primary.TryOwn(),"Primary GUI instance owns the user/session mutex");
            // Duplicates arrive before the HWND is published, as on a slow first startup.
            Task<int>[] waiting=[Duplicate(),Duplicate()];await Task.Delay(200);
            primary.Attach(this,ActivateExistingWindow);
            check((await Task.WhenAll(waiting)).All(x=>x==0)&&primary.ActivationCount==2,"Concurrent startup requests wait for and activate one existing window");
            WindowState=WindowState.Minimized;
            check(await Duplicate()==0&&IsVisible&&WindowState==WindowState.Normal,"A duplicate exits after restoring the minimized primary window");
            WindowState=WindowState.Maximized;WindowState=WindowState.Minimized;
            check(await Duplicate()==0&&WindowState==WindowState.Normal,"Activating restores the fixed-size window without maximizing");
            WindowState=WindowState.Normal;
            SetupTray();Hide();
            check(await Duplicate()==0&&IsVisible&&WindowState==WindowState.Normal,"A duplicate restores the hidden tray window without a second GUI");
            check(primary.ActivationCount==5,"Every duplicate was acknowledged by the same primary instance");
            check(SingleInstance.IsForeground(this),"Duplicate launch brings the existing window to the foreground");
        }
        using(var restarted=new SingleInstance(scope))check(restarted.TryOwn(),"Exiting the owner permits the next launch to own the instance");
        string crashedScope=Guid.NewGuid().ToString("N");
        using var afterCrash=new SingleInstance(crashedScope);using var abandoned=new Mutex(false,afterCrash.MutexName);
        var thread=new Thread(()=>abandoned.WaitOne());thread.Start();thread.Join();
        check(afterCrash.TryOwn(),"An abandoned mutex after a crash does not block restart");
        DisposeTray();
    }
}
