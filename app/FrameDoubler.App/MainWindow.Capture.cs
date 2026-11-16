using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using FrameDoubler.Core;
using Forms=System.Windows.Forms;

namespace FrameDoubler.App;

// Capture preview page: page switching, shortcuts (window and optional global) and the
// immersive full-screen view. The conversion page keeps its original code untouched.
public partial class MainWindow
{
    private CaptureView? capture;
    private string page="";
    private bool immersive;
    private Rect immersiveRestoreBounds;
    private WindowState immersiveRestoreState;
    private HwndSource? hotkeySource;
    private readonly List<(int id,string action)> hotkeys=new();
    internal CaptureView? Capture=>capture;
    internal string CurrentPage=>page;
    internal bool CaptureRecording=>capture?.Recording==true||capture?.Saving==true||capture?.StartingRecording==true;

    [DllImport("user32.dll",SetLastError=true)] private static extern bool RegisterHotKey(IntPtr hwnd,int id,uint modifiers,uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd,int id);
    [DllImport("user32.dll",SetLastError=true)] private static extern bool SetWindowPos(IntPtr hwnd,IntPtr after,int x,int y,int width,int height,uint flags);

    private void InitializeCapture(){
        capture=new CaptureView(this);CaptureHost.Content=capture;
        capture.ImmersiveChanged+=SetImmersive;
        PreviewKeyDown+=CaptureKeyDown;PreviewKeyUp+=CaptureKeyUp;
        Loaded+=(_,_)=>ShowPage(preferences.StartPage);
        SourceInitialized+=(_,_)=>{hotkeySource=HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);hotkeySource?.AddHook(HotkeyHook);ApplyShortcuts();};
        Closed+=(_,_)=>UnregisterHotkeys();
    }
    private void TranslateCapture(){
        CapturePageButton.Content=T("캡처 미리보기","Capture preview");ConvertPageButton.Content=T("파일 변환","Convert files");
        capture?.Translate();
    }
    private void PageChecked(object sender,RoutedEventArgs e){if(sender is RadioButton{Tag:string value}&&IsLoaded)ShowPage(value);}
    internal void ShowPage(string value){
        value=value=="convert"?"convert":"capture";
        if(value==page)return;
        page=value;bool live=page=="capture";
        CapturePageButton.IsChecked=live;ConvertPageButton.IsChecked=!live;
        CaptureHost.Visibility=live?Visibility.Visible:Visibility.Collapsed;
        foreach(var element in new FrameworkElement[]{ConvertHeader,ConvertBody,ConvertFooter})element.Visibility=live?Visibility.Collapsed:Visibility.Visible;
        if(live)_=capture?.ActivateAsync();else capture?.Deactivate();
        if(preferences.StartPage!=page)ChangeSettings(p=>p with{StartPage=page});
    }
    internal void LogCapture(string message)=>Log(message);
    internal void OpenSettings(string? category){
        if(busy)return;
        new SettingsWindow(this,category).ShowDialog();
        capture?.SettingsChanged();ApplyShortcuts();RefreshShortcutHints();
    }

    // ---- shortcuts
    private void CaptureKeyDown(object sender,KeyEventArgs e){
        if(page!="capture"||capture is null||Keyboard.FocusedElement is TextBox)return;
        var key=e.Key==Key.System?e.SystemKey:e.Key;
        if(e.IsRepeat&&key!=Key.Escape)return;
        if(capture.HandleShortcut(key,Keyboard.Modifiers))e.Handled=true;
    }
    private void CaptureKeyUp(object sender,KeyEventArgs e){
        if(page!="capture"||capture is null)return;
        if(capture.HandleKeyUp(e.Key==Key.System?e.SystemKey:e.Key))e.Handled=true;
    }
    // Global shortcuts also work while a game or another program is in front. Holding the
    // "original" key needs key-up events, so it stays a window shortcut.
    internal void ApplyShortcuts(){
        UnregisterHotkeys();
        if(!preferences.GlobalShortcuts||hotkeySource is null)return;
        int id=0x4644;var failed=new List<string>();
        foreach(var (action,text) in new[]{("screenshot",preferences.ShortcutScreenshot),("record",preferences.ShortcutRecord),("fullscreen",preferences.ShortcutFullscreen),
                    ("fg",preferences.ShortcutFrameGeneration),("stats",preferences.ShortcutStats)}){
            if(!Shortcut.TryParse(text,out var modifiers,out var key))continue;
            uint m=0x4000;   // MOD_NOREPEAT
            if(modifiers.HasFlag(ModifierKeys.Alt))m|=1;if(modifiers.HasFlag(ModifierKeys.Control))m|=2;
            if(modifiers.HasFlag(ModifierKeys.Shift))m|=4;if(modifiers.HasFlag(ModifierKeys.Windows))m|=8;
            if(RegisterHotKey(hotkeySource.Handle,id,m,(uint)KeyInterop.VirtualKeyFromKey(key)))hotkeys.Add((id,action));else failed.Add(text);
            id++;
        }
        if(failed.Count>0)Log(T("다른 프로그램이 쓰고 있어 전역 단축키로 등록하지 못했습니다: ","Could not register as global shortcuts (used by another program): ")+string.Join(", ",failed));
    }
    private void UnregisterHotkeys(){
        if(hotkeySource is not null)foreach(var (id,_) in hotkeys)UnregisterHotKey(hotkeySource.Handle,id);
        hotkeys.Clear();
    }
    private IntPtr HotkeyHook(IntPtr hwnd,int msg,IntPtr wParam,IntPtr lParam,ref bool handled){
        if(msg==0x0312){   // WM_HOTKEY
            var action=hotkeys.FirstOrDefault(h=>h.id==wParam.ToInt32()).action;
            if(action is not null&&capture is not null){capture.RunShortcut(action);handled=true;}
        }
        return IntPtr.Zero;
    }

    // ---- immersive full screen: the preview covers the whole monitor, including the taskbar.
    private void SetImmersive(bool on){
        if(immersive==on)return;
        immersive=on;
        var chrome=WindowChrome.GetWindowChrome(this);
        if(on){
            immersiveRestoreState=WindowState;immersiveRestoreBounds=new Rect(Left,Top,Width,Height);
            TitleRow.Height=new GridLength(0);TitleBar.Visibility=Visibility.Collapsed;
            if(chrome is not null){chrome.CaptionHeight=0;chrome.ResizeBorderThickness=new Thickness(0);}
            WindowBorder.BorderThickness=new Thickness(0);WindowBorder.CornerRadius=new CornerRadius(0);
            var screen=Forms.Screen.FromHandle(new WindowInteropHelper(this).Handle);
            var transform=PresentationSource.FromVisual(this)?.CompositionTarget.TransformFromDevice??Matrix.Identity;
            var topLeft=transform.Transform(new Point(screen.Bounds.Left,screen.Bounds.Top));
            var bottomRight=transform.Transform(new Point(screen.Bounds.Right,screen.Bounds.Bottom));
            WindowState=WindowState.Normal;
            MoveWindow(new Rect(topLeft,bottomRight));
            Activate();
        }else{
            // Shrink first, while the page still has its full-screen layout: restoring the title
            // bar and panels before the resize made WM_SIZE lay out and render the whole page
            // synchronously (about 0.85 s), with the full-screen preview child clipped meanwhile.
            MoveWindow(immersiveRestoreBounds);
            WindowState=immersiveRestoreState;
            UpdateLayout();   // the preview child follows the smaller window at once (whole picture, not a corner)
            TitleRow.Height=new GridLength(52);TitleBar.Visibility=Visibility.Visible;
            if(chrome is not null){chrome.CaptionHeight=48*UiScale;chrome.ResizeBorderThickness=new Thickness(0);}
            WindowBorder.BorderThickness=new Thickness(1);
            WindowBorder.CornerRadius=new CornerRadius(WindowState==WindowState.Maximized?0:12);
            capture?.SetImmersive(false);
            FitFixedWindow(false);
        }
        SetPreviewWindowFrame(on);
    }
    // Position and size change in one SetWindowPos. Setting Left, Top, Width and Height one by
    // one passed through intermediate windows (the restored width but still screen-high, hanging
    // below the monitor), each with its own WM_SIZE layout (tools/validation/live-gui
    // --immersive-transition measures the transition).
    private void MoveWindow(Rect bounds){
        var toDevice=PresentationSource.FromVisual(this)?.CompositionTarget.TransformToDevice??Matrix.Identity;
        var topLeft=toDevice.Transform(bounds.TopLeft);var bottomRight=toDevice.Transform(bounds.BottomRight);
        int x=(int)Math.Round(topLeft.X),y=(int)Math.Round(topLeft.Y),width=(int)Math.Round(bottomRight.X)-x,height=(int)Math.Round(bottomRight.Y)-y;
        const uint NoZOrder=0x0004,NoActivate=0x0010;
        if(width>0&&height>0&&SetWindowPos(new WindowInteropHelper(this).Handle,IntPtr.Zero,x,y,width,height,NoZOrder|NoActivate))return;
        Left=bounds.Left;Top=bounds.Top;Width=bounds.Width;Height=bounds.Height;
    }
}
