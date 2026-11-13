using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Shell;
using System.Windows.Threading;

namespace FrameDoubler.App;

public partial class MainWindow
{
    public async Task<object> CheckUiAsync(){
        var passed=new List<string>();
        void Check(bool condition,string name){if(!condition)throw new InvalidOperationException("UI: "+name);passed.Add(name);}
        async Task Idle(){await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);}
        Check(WindowStyle==WindowStyle.None&&WindowChrome.GetWindowChrome(this).ResizeBorderThickness.Left>0,"Frameless and resizable chrome");
        int picks=0;FilePicker=()=>{picks++;return[];};
        var peer=new ButtonAutomationPeer(EmptyPanel);((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();await Idle();
        Check(picks==1,"Drop target click invokes file chooser");
        ApplicationCommands.Open.Execute(null,this);await Idle();Check(picks==2,"Ctrl+O command invokes file chooser");
        FilePicker=null;
        foreach(var size in new[]{new Size(1180,800),new Size(900,640),new Size(820,480)})foreach(string lang in new[]{"ko","en"}){
            Width=size.Width;Height=size.Height;PreviewLanguage(lang);Advanced.IsExpanded=true;await Idle();UpdateLayout();
            var bar=(ScrollBar)SettingsScroll.Template.FindName("PART_VerticalScrollBar",SettingsScroll);
            var barBounds=bar.TransformToAncestor(SettingsScroll).TransformBounds(new Rect(bar.RenderSize));
            var contentBounds=SettingsPanel.TransformToAncestor(SettingsScroll).TransformBounds(new Rect(SettingsPanel.RenderSize));
            Check(contentBounds.Right<=barBounds.Left-7,$"Settings gutter {size.Width}x{size.Height} {lang}");
            SettingsScroll.ScrollToBottom();await Idle();UpdateLayout();
            Check(SettingsScroll.VerticalOffset>0&&SettingsScroll.VerticalOffset+SettingsScroll.ViewportHeight>=SettingsScroll.ExtentHeight-2,$"Advanced settings scroll to bottom {size.Width} {lang}");
            var drop=DropBorder.TransformToAncestor(WindowBorder).TransformBounds(new Rect(DropBorder.RenderSize));
            var footer=StartButton.TransformToAncestor(WindowBorder).TransformBounds(new Rect(StartButton.RenderSize));
            Check(drop.Bottom<footer.Top&&drop.Width>250,$"Content stays above footer {size.Width} {lang}");
            var hint=DropHint.TransformToAncestor(DropBorder).TransformBounds(new Rect(DropHint.RenderSize));
            Check(hint.Bottom<DropBorder.ActualHeight-3,$"Drop hint remains visible {size.Width} {lang}");
            Check(OutputBox.ActualWidth>=150,$"Output path has usable width {size.Width} {lang}");
            Advanced.IsExpanded=false;SettingsScroll.ScrollToTop();
        }
        Width=1180;Height=800;await Idle();
        MaximizeButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));await Idle();
        Check(WindowState==WindowState.Maximized,"Maximize button");
        Check(ActualWidth<=SystemParameters.WorkArea.Width+2&&ActualHeight<=SystemParameters.WorkArea.Height+2,"Maximize respects taskbar work area");
        MaximizeButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));await Idle();Check(WindowState==WindowState.Normal,"Restore button");
        MinimizeButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));await Idle();Check(WindowState==WindowState.Minimized,"Minimize button");
        WindowState=WindowState.Normal;
        return new{Assertions=passed.Count,Passed=passed};
    }
}
