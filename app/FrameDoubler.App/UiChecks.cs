using System;
using System.Collections.Generic;
using System.Linq;
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
        Check(VerifyCheck.IsChecked==false,"Verification starts unchecked");
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
            EstimateValue.Text=lang=="ko"?"약 25시간 59분 ~ 61시간 47분":"About 25 h 59 min ~ 61 h 47 min";
            EstimateHint.Text=lang=="ko"?"최근 실제 작업으로 보정 · 검증 포함":"Adjusted from recent jobs · Includes verification";await Idle();UpdateLayout();
            foreach(var text in new[]{EstimateValue,EstimateHint}){
                var bounds=text.TransformToAncestor(SettingsPanel).TransformBounds(new Rect(text.RenderSize));
                Check(bounds.Right<=SettingsPanel.ActualWidth+1&&text.ActualWidth>0,$"Estimate text fits {text.Name} {size.Width} {lang}");
            }
            RefreshEstimateText();
            Advanced.IsExpanded=false;SettingsScroll.ScrollToTop();
        }
        Width=1180;Height=800;PreviewLanguage("ko");await Idle();
        EnglishButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));await Idle();
        Check(English&&AddButton.Content as string=="Add videos","Language switch selects English");
        KoreanButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));await Idle();
        Check(!English&&AddButton.Content as string=="영상 추가","Language switch selects Korean");
        Check(LanguageThumb.ActualWidth>20&&Math.Abs(LanguageThumb.ActualWidth*2-(LanguageSwitch.ActualWidth-6))<4,"Language switch halves");
        MaximizeButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));await Idle();
        Check(WindowState==WindowState.Maximized,"Maximize button");
        Check(ActualWidth<=SystemParameters.WorkArea.Width+2&&ActualHeight<=SystemParameters.WorkArea.Height+2,"Maximize respects taskbar work area");
        MaximizeButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));await Idle();Check(WindowState==WindowState.Normal,"Restore button");
        // Minimize, close and Alt+F4 all ask first; cancelling keeps the window.
        var asked=new List<ExitTrigger>();var answer=DialogChoice.Cancel;ExitPrompt=trigger=>{asked.Add(trigger);return answer;};
        MinimizeButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));await Idle();
        Check(asked.Count==1&&asked[0]==ExitTrigger.Minimize&&IsVisible&&WindowState==WindowState.Normal,"Minimize asks before hiding to the tray");
        CloseButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));await Idle();
        Check(asked.Count==2&&asked[1]==ExitTrigger.Close&&IsVisible,"Close button asks and cancel keeps the window");
        Close();await Idle();
        Check(asked.Count==3&&asked[2]==ExitTrigger.Close&&IsVisible,"Alt+F4 / taskbar close also asks");
        SetupTray();answer=DialogChoice.Secondary;
        MinimizeButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));await Idle();
        Check(!IsVisible&&tray?.Visible==true,"Minimize to tray hides the window and keeps the tray icon");
        Restore();await Idle();Check(IsVisible&&WindowState==WindowState.Normal,"Tray restore");
        tray?.Dispose();tray=null;ExitPrompt=null;
        // Branded dialogs stay compact and every button fits, in both languages and for every trigger.
        foreach(string lang in new[]{"ko","en"}){
            PreviewLanguage(lang);
            foreach(var trigger in new[]{ExitTrigger.Close,ExitTrigger.Minimize,ExitTrigger.Tray}){
                var dialog=CreateExitDialog(trigger);dialog.ShowActivated=false;dialog.Show();await Idle();dialog.UpdateLayout();
                var root=(FrameworkElement)dialog.Content;var choices=dialog.Choices;
                Check(choices.Count==(trigger==ExitTrigger.Tray?2:3),$"Exit dialog choices {trigger} {lang}");
                foreach(var button in choices){
                    var bounds=button.TransformToAncestor(root).TransformBounds(new Rect(button.RenderSize));
                    // DesiredSize includes the button's left margin.
                    Check(bounds.Left>=0&&bounds.Right<=root.ActualWidth-20&&button.ActualWidth+button.Margin.Left+button.Margin.Right+.5>=button.DesiredSize.Width,$"Exit dialog button fits {button.Content} {trigger} {lang}");
                }
                Check(dialog.ActualWidth<=470&&dialog.ActualHeight<=340,$"Exit dialog is compact {trigger} {lang}");
                dialog.Close();
            }
        }
        // Settings: every category renders with its controls inside the page, in both languages.
        static System.Collections.Generic.IEnumerable<DependencyObject> Tree(DependencyObject root){
            for(int i=0;i<System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);i++){var child=System.Windows.Media.VisualTreeHelper.GetChild(root,i);yield return child;foreach(var x in Tree(child))yield return x;}
        }
        foreach(string lang in new[]{"ko","en"}){
            PreviewLanguage(lang);
            var settings=new SettingsWindow(this){ShowActivated=false};settings.Show();await Idle();
            Check(settings.Navigation.Items.Count==4,$"Settings categories {lang}");
            foreach(System.Windows.Controls.ListBoxItem item in settings.Navigation.Items){
                settings.Navigation.SelectedItem=item;await Idle();settings.UpdateLayout();
                var view=settings.Page!;string name=System.Windows.Automation.AutomationProperties.GetName(item);
                var controls=Tree(view).OfType<System.Windows.Controls.Control>().Where(x=>x is System.Windows.Controls.Button or System.Windows.Controls.ComboBox or System.Windows.Controls.CheckBox).ToList();
                Check(view.ActualHeight>0&&controls.Count>0,$"Settings page has controls {name} {lang}");
                foreach(var control in controls){
                    var bounds=control.TransformToAncestor(view).TransformBounds(new Rect(control.RenderSize));
                    Check(bounds.Left>=0&&bounds.Right<=view.ActualWidth+1,$"Settings control fits {name} {control.GetType().Name} {lang}");
                }
            }
            settings.Close();
        }
        PreviewLanguage("ko");
        // A remembered close action skips the dialog and is saved to settings.json.
        ChangeSettings(p=>p with{CloseAction="tray"});SetupTray();ExitPrompt=null;
        Check(FrameDoubler.Core.Preferences.Load().CloseAction=="tray","Close action setting is saved");
        CloseButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));await Idle();
        Check(!IsVisible,"Remembered close action minimizes to the tray without asking");
        Restore();await Idle();ChangeSettings(p=>p with{CloseAction="ask"});tray?.Dispose();tray=null;
        return new{Assertions=passed.Count,Passed=passed};
    }
}
