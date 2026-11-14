using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;

namespace FrameDoubler.App;

// A per-user, per-login desktop instance, shared across installation directories.
// The mutex is acquired/released on the WPF thread. No settings are read by a duplicate.
internal sealed class SingleInstance:IDisposable
{
    private readonly Mutex mutex;
    private readonly string marker;
    private readonly uint message;
    private bool owned,disposed;
    private nint handle;
    private HwndSource? source;
    private Func<bool>? activate;
    internal string MutexName{get;}
    internal int ActivationCount{get;private set;}
    internal SingleInstance(string? testScope=null){
        using var identity=WindowsIdentity.GetCurrent();
        string sid=identity.User?.Value??throw new InvalidOperationException("Cannot identify the desktop user");
        // Diagnostics can exercise the real mechanism without activating the user's live app.
        string scope=testScope??Environment.GetEnvironmentVariable("FRAMEDOUBLER_INSTANCE_TEST_SCOPE")??"main";
        string key=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope)))[..16];
        marker=$"FrameDoubler.Window.{sid}.{Process.GetCurrentProcess().SessionId}.{key}";
        MutexName="Local\\"+marker;mutex=new Mutex(false,MutexName);
        message=RegisterWindowMessage(marker+".Activate");
        if(message==0)throw new System.ComponentModel.Win32Exception();
    }
    internal bool TryOwn(){
        if(owned)return true;
        try{owned=mutex.WaitOne(0);}catch(AbandonedMutexException){owned=true;}
        return owned;
    }
    internal async Task<bool> AcquireOrActivateAsync(){
        if(TryOwn())return true;
        var timer=Stopwatch.StartNew();
        do{
            nint existing=0;
            EnumWindows((window,_)=>{if(GetProp(window,marker)==1){existing=window;return false;}return true;},0);
            if(existing!=0){
                GetWindowThreadProcessId(existing,out uint pid);AllowSetForegroundWindow(pid);
                // Wait for an acknowledgement: a window already closing does not consume the launch.
                if(SendMessageTimeout(existing,message,0,0,0x22,300,out var result)!=0&&result==1)return false;
            }
            await Task.Delay(100);
            if(TryOwn())return true;
        }while(timer.Elapsed<TimeSpan.FromSeconds(15));
        throw new TimeoutException("기존 FrameDoubler 창에 연결하지 못했습니다. 잠시 후 다시 실행해 주세요. / Cannot activate the existing FrameDoubler window. Try again shortly.");
    }
    internal void Attach(Window window,Func<bool> onActivate){
        if(!owned)throw new InvalidOperationException("Only the primary instance can receive activation");
        handle=new WindowInteropHelper(window).Handle;
        if(handle==0)throw new InvalidOperationException("The window handle is not ready");
        activate=onActivate;source=HwndSource.FromHwnd(handle)??throw new InvalidOperationException("No WPF window source");
        source.AddHook(Hook);
        if(!SetProp(handle,marker,1))throw new System.ComponentModel.Win32Exception();
        // Harmless activation only; permits an ordinary launch to restore an elevated copy.
        ChangeWindowMessageFilterEx(handle,message,1,0);
    }
    private nint Hook(nint window,int msg,nint wParam,nint lParam,ref bool handled){
        if((uint)msg!=message)return 0;
        handled=true;
        if(activate?.Invoke()!=true)return 0;
        ActivationCount++;return 1;
    }
    internal static void Foreground(Window window)=>SetForegroundWindow(new WindowInteropHelper(window).Handle);
    internal static bool IsForeground(Window window)=>GetForegroundWindow()==new WindowInteropHelper(window).Handle;
    public void Dispose(){
        if(disposed)return;disposed=true;
        if(source is {IsDisposed:false})source.RemoveHook(Hook);
        if(handle!=0)RemoveProp(handle,marker);
        if(owned)mutex.ReleaseMutex();mutex.Dispose();
    }
    private delegate bool EnumerateWindow(nint window,nint parameter);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")]private static extern bool EnumWindows(EnumerateWindow callback,nint parameter);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern nint GetProp(nint window,string name);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool SetProp(nint window,string name,nint value);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern nint RemoveProp(nint window,string name);
    [DllImport("user32.dll")]private static extern uint GetWindowThreadProcessId(nint window,out uint pid);
    [DllImport("user32.dll")]private static extern bool AllowSetForegroundWindow(uint pid);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern nint SendMessageTimeout(nint window,uint message,nint wParam,nint lParam,uint flags,uint milliseconds,out nuint result);
    [DllImport("user32.dll")]private static extern bool ChangeWindowMessageFilterEx(nint window,uint message,uint action,nint filter);
    [DllImport("user32.dll")]private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")]private static extern nint GetForegroundWindow();
}
