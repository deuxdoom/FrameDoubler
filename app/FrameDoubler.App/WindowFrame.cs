using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace FrameDoubler.App;

internal static class WindowFrame
{
    [StructLayout(LayoutKind.Sequential)]private struct Point{public int X,Y;}
    [StructLayout(LayoutKind.Sequential)]private struct Rect{public int Left,Top,Right,Bottom;}
    [StructLayout(LayoutKind.Sequential)]private struct MinMax{public Point Reserved,MaxSize,MaxPosition,MinTrack,MaxTrack;}
    [StructLayout(LayoutKind.Sequential)]private struct Monitor{public int Size;public Rect Bounds,Work;public uint Flags;}
    [DllImport("user32.dll")]private static extern nint MonitorFromWindow(nint window,uint flags);
    [DllImport("user32.dll",CharSet=CharSet.Auto)]private static extern bool GetMonitorInfo(nint monitor,ref Monitor info);
    [DllImport("dwmapi.dll")]private static extern int DwmSetWindowAttribute(nint window,int attribute,ref int value,int size);
    // Rounded Windows 11 corners only, for fixed-size dialogs that size themselves to their content.
    public static void RoundCorners(Window window)=>RoundCorners(new WindowInteropHelper(window).Handle);
    public static void RoundCorners(nint handle){int rounded=2;_=DwmSetWindowAttribute(handle,33,ref rounded,sizeof(int));}
    public static void Attach(Window window){
        nint handle=new WindowInteropHelper(window).Handle;
        HwndSource.FromHwnd(handle)?.AddHook(Hook);
        RoundCorners(window);
        var area=SystemParameters.WorkArea;window.Width=Math.Min(window.Width,area.Width-24);window.Height=Math.Min(window.Height,area.Height-24);
    }
    private static nint Hook(nint window,int message,nint wParam,nint lParam,ref bool handled){
        if(message==0x24){
            var info=new Monitor{Size=Marshal.SizeOf<Monitor>()};
            if(GetMonitorInfo(MonitorFromWindow(window,2),ref info)){
                var size=Marshal.PtrToStructure<MinMax>(lParam);
                size.MaxPosition=new(){X=info.Work.Left-info.Bounds.Left,Y=info.Work.Top-info.Bounds.Top};
                size.MaxSize=new(){X=info.Work.Right-info.Work.Left,Y=info.Work.Bottom-info.Work.Top};
                Marshal.StructureToPtr(size,lParam,false);handled=true;
            }
        }
        return 0;
    }
}
