using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Runtime.InteropServices;
using Forms=System.Windows.Forms;

namespace FrameDoubler.App;

public partial class MainWindow
{
    private const double CompactWidth=1180,CompactHeight=740;
    internal double UiScale=>Math.Min(1,Math.Min(Math.Max(1,ActualWidth)/CompactWidth,Math.Max(1,ActualHeight)/CompactHeight));
    internal static Size FixedWindowSize(Size workArea){
        double fit=Math.Min(1,Math.Min(Math.Max(1,workArea.Width-32)/CompactWidth,Math.Max(1,workArea.Height-32)/CompactHeight));
        return new(CompactWidth*fit,CompactHeight*fit);
    }
    private void InitializeFixedWindow(){
        HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook((nint hwnd,int message,nint wParam,nint lParam,ref bool handled)=>{
            if(message is 0x02E0 or 0x007E or 0x0232)Dispatcher.BeginInvoke(()=>FitFixedWindow(false));
            return 0;
        });
        SetPreviewWindowFrame(false);
        FitFixedWindow(true);
    }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd,int attribute,ref uint value,int size);
    private int nativeBorderStatus;
    private void SetPreviewWindowFrame(bool fullScreen){
        var handle=new WindowInteropHelper(this).Handle;
        uint border=0xfffffffe,corner=fullScreen?1u:2u; // No DWM border; square full screen.
        nativeBorderStatus=DwmSetWindowAttribute(handle,34,ref border,4);
        DwmSetWindowAttribute(handle,33,ref corner,4);
    }
    private void FitFixedWindow(bool center){
        if(immersive)return;
        var screen=Forms.Screen.FromHandle(new WindowInteropHelper(this).Handle);
        var dpi=VisualTreeHelper.GetDpi(this);
        var work=new Rect(screen.WorkingArea.Left/dpi.DpiScaleX,screen.WorkingArea.Top/dpi.DpiScaleY,screen.WorkingArea.Width/dpi.DpiScaleX,screen.WorkingArea.Height/dpi.DpiScaleY);
        var size=FixedWindowSize(work.Size);Width=size.Width;Height=size.Height;
        if(center){Left=work.Left+(work.Width-Width)/2;Top=work.Top+(work.Height-Height)/2;}
        else{Left=Math.Clamp(Left,work.Left+8,Math.Max(work.Left+8,work.Right-Width-8));Top=Math.Clamp(Top,work.Top+8,Math.Max(work.Top+8,work.Bottom-Height-8));}
        UpdateCompactLayout();
    }
    private void UpdateCompactLayout(){
        if(immersive)return;
        double scale=.9*UiScale;
        foreach(var panel in new FrameworkElement[]{TitleBar,ConvertHeader,ConvertBody,ConvertFooter})panel.LayoutTransform=new ScaleTransform(scale,scale);
        TitleRow.Height=new GridLength(48*UiScale);
        var chrome=WindowChrome.GetWindowChrome(this);
        if(chrome is not null){chrome.CaptionHeight=48*UiScale;chrome.ResizeBorderThickness=new Thickness(0);}
        capture?.RefreshLayout();
    }
}
