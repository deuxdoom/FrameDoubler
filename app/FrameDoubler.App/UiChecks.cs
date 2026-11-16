using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;

namespace FrameDoubler.App;

public partial class MainWindow
{
    public async Task<object> CheckUiAsync(){
        var passed=new List<string>();
        void Check(bool condition,string name){if(!condition)throw new InvalidOperationException("UI: "+name);passed.Add(name);}
        async Task Idle(){await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);}
        // These checks exercise the conversion page; fresh settings open on capture.
        ShowPage("convert");await Idle();UpdateLayout();
        Check(WindowStyle==WindowStyle.None&&ResizeMode==ResizeMode.CanMinimize&&WindowChrome.GetWindowChrome(this).ResizeBorderThickness.Left==0,"Frameless fixed-size window with minimize");
        Check(VerifyCheck.IsChecked==false,"Verification starts unchecked");
        foreach(var weight in new[]{FontWeights.Normal,FontWeights.Medium,FontWeights.SemiBold,FontWeights.Bold}){
            var face=new Typeface(FontFamily,FontStyles.Normal,weight,FontStretches.Normal);
            bool loaded=face.TryGetGlyphTypeface(out var glyph);
            const string sample="FrameDoubler 30fps → 60fps · 캡처 미리보기 프레임 생성 녹화 저장 힣 · 日本語 キャプチャ 録画 音声 同期";
            Check(loaded&&glyph.FontUri.ToString().Contains("pretendardjpvariable.ttf",StringComparison.OrdinalIgnoreCase)&&
                glyph.Weight==weight&&!face.IsBoldSimulated&&sample.All(c=>glyph.CharacterToGlyphMap.TryGetValue(c,out var index)&&index!=0),
                "Embedded Pretendard JP resolves actual weight and Latin/Hangul/Japanese glyphs "+weight.ToOpenTypeWeight());
        }
        int picks=0;FilePicker=()=>{picks++;return[];};
        var peer=new ButtonAutomationPeer(EmptyPanel);((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();await Idle();
        Check(picks==1,"Drop target click invokes file chooser");
        Keyboard.ClearFocus();
        Check(HandleConvertShortcut(Key.O,ModifierKeys.Control),"Ctrl+O shortcut is handled on the conversion page");await Idle();Check(picks==2,"Ctrl+O shortcut invokes file chooser");
        FilePicker=null;
        // Conversion list shortcuts work on placeholder entries; nothing is analyzed or converted.
        foreach(string name in new[]{"a","b","c"})Entries.Add(new QueueEntry(System.IO.Path.Combine(@"X:\ui-check",name+".mp4"),T));
        await Idle();UpdateLayout();string Order()=>string.Join(",",Entries.Select(x=>x.Name[..1]));
        QueueList.SelectedItem=Entries[2];
        Check(HandleConvertShortcut(Key.Up,ModifierKeys.Alt)&&Order()=="a,c,b"&&QueueList.SelectedItems.Count==1&&((QueueEntry)QueueList.SelectedItem).Name=="c.mp4","Alt+Up moves the selected video up and keeps it selected");
        Check(HandleConvertShortcut(Key.Up,ModifierKeys.Alt)&&HandleConvertShortcut(Key.Up,ModifierKeys.Alt)&&Order()=="c,a,b","Moving up stops at the top of the list");
        Check(HandleConvertShortcut(Key.Down,ModifierKeys.Alt)&&Order()=="a,c,b","Alt+Down moves the selected video down");
        Check(HandleConvertShortcut(Key.A,ModifierKeys.Control)&&QueueList.SelectedItems.Count==3,"Ctrl+A selects every video");
        Check(HandleConvertShortcut(Key.Down,ModifierKeys.Alt)&&Order()=="a,c,b","Moving a full selection keeps the order");
        QueueList.UnselectAll();QueueList.SelectedItem=Entries[1];await Idle();
        bool removed=HandleConvertShortcut(Key.Delete,ModifierKeys.None);await Idle();
        Check(removed&&Order()=="a,b"&&(QueueList.SelectedItem as QueueEntry)?.Name=="b.mp4",
            $"Delete removes the selection and selects the next video (handled {removed}, order {Order()}, selected {(QueueList.SelectedItem as QueueEntry)?.Name}, focus {Keyboard.FocusedElement?.GetType().Name})");
        Check(!HandleConvertShortcut(Key.Enter,ModifierKeys.None)||QueueList.IsKeyboardFocusWithin,"A bare Enter acts on the list only from inside it");
        OutputBox.Focus();await Idle();
        Check(!HandleConvertShortcut(Key.Delete,ModifierKeys.None)&&!HandleConvertShortcut(Key.A,ModifierKeys.Control)&&Order()=="a,b","Text boxes keep Delete and Ctrl+A");
        FilePicker=()=>{picks++;return[];};
        Check(HandleConvertShortcut(Key.O,ModifierKeys.Control)&&picks==3,"Ctrl+O still adds videos while typing in a text box");
        FilePicker=null;
        Keyboard.ClearFocus();
        var saved=preferences;preferences=preferences with{ShortcutRemove="",ShortcutConvertAll="",ShortcutConvertSelected="Ctrl+Return"};
        Check(!HandleConvertShortcut(Key.Delete,ModifierKeys.None)&&Order()=="a,b"&&HandleConvertShortcut(Key.Enter,ModifierKeys.Control|ModifierKeys.Shift)==false&&HandleConvertShortcut(Key.Enter,ModifierKeys.Control),
            "A cleared shortcut does nothing; an older Return spelling still matches Enter");
        preferences=saved;Entries.Clear();UpdateControls();
        // Double-clicking a converted video plays its result; an unconverted one does nothing.
        string played=System.IO.Path.Combine(System.IO.Path.GetTempPath(),$"fd-ui-{Guid.NewGuid():N}.mp4");System.IO.File.WriteAllBytes(played,[0]);
        try{
            var opened=new List<string>();Opener=opened.Add;
            Entries.Add(new QueueEntry(System.IO.Path.Combine(@"X:\ui-check","done.mp4"),T){Output=played,State="unverified"});
            Entries.Add(new QueueEntry(System.IO.Path.Combine(@"X:\ui-check","pending.mp4"),T));
            await Idle();UpdateLayout();
            MouseButtonEventArgs DoubleClick(QueueEntry entry)=>new(Mouse.PrimaryDevice,0,MouseButton.Left){RoutedEvent=System.Windows.Controls.Control.MouseDoubleClickEvent,
                Source=QueueList.ItemContainerGenerator.ContainerFromItem(entry)};
            QueueList.RaiseEvent(DoubleClick(Entries[1]));QueueList.RaiseEvent(DoubleClick(Entries[0]));
            Check(opened.SequenceEqual([played]),"Double-clicking a converted video plays its result; an unconverted one does nothing");
        }finally{Opener=null;Entries.Clear();UpdateControls();System.IO.File.Delete(played);}
        foreach(var size in new[]{new Size(1180,800),new Size(900,640),new Size(820,480)})foreach(string lang in new[]{"ko","en"}){
            Width=size.Width;Height=size.Height;PreviewLanguage(lang);await Idle();UpdateLayout();
            var bar=(ScrollBar)SettingsScroll.Template.FindName("PART_VerticalScrollBar",SettingsScroll);
            var barBounds=bar.TransformToAncestor(SettingsScroll).TransformBounds(new Rect(bar.RenderSize));
            var contentBounds=SettingsPanel.TransformToAncestor(SettingsScroll).TransformBounds(new Rect(SettingsPanel.RenderSize));
            // Advanced options moved to Settings, so the panel may fit without a scroll bar.
            Check(bar.IsVisible?contentBounds.Right<=barBounds.Left-7:contentBounds.Right<=SettingsScroll.ActualWidth+1,$"Settings gutter {size.Width}x{size.Height} {lang}");
            SettingsScroll.ScrollToBottom();await Idle();UpdateLayout();
            Check(SettingsScroll.VerticalOffset+SettingsScroll.ViewportHeight>=SettingsScroll.ExtentHeight-2,$"Output settings reachable to the bottom {size.Width} {lang}");
            var drop=DropBorder.TransformToAncestor(WindowBorder).TransformBounds(new Rect(DropBorder.RenderSize));
            var footer=StartButton.TransformToAncestor(WindowBorder).TransformBounds(new Rect(StartButton.RenderSize));
            Check(drop.Bottom<footer.Top&&drop.Width>250,$"Content stays above footer {size.Width} {lang}");
            var selected=SelectedButton.TransformToAncestor(WindowBorder).TransformBounds(new Rect(SelectedButton.RenderSize));
            var cancel=CancelButton.TransformToAncestor(WindowBorder).TransformBounds(new Rect(CancelButton.RenderSize));
            Check(cancel.Right<=selected.Left&&selected.Right<=footer.Left&&footer.Right<=WindowBorder.ActualWidth,$"Footer buttons do not overlap {size.Width} {lang}");
            foreach(var button in new[]{CancelButton,SelectedButton,StartButton}){
                var label=new System.Windows.Controls.TextBlock{Text=(string)button.Content,FontSize=button.FontSize,FontFamily=button.FontFamily,FontWeight=button.FontWeight};
                label.Measure(new Size(double.PositiveInfinity,double.PositiveInfinity));
                Check(label.DesiredSize.Width+button.Padding.Left+button.Padding.Right+2<=button.ActualWidth+.5,$"Footer label fits {button.Name} {size.Width} {lang}");
            }
            var hint=DropHint.TransformToAncestor(DropBorder).TransformBounds(new Rect(DropHint.RenderSize));
            Check(hint.Bottom<DropBorder.ActualHeight-3,$"Drop hint remains visible {size.Width} {lang}");
            Check(OutputBox.ActualWidth>=150,$"Output path has usable width {size.Width} {lang}");
            EstimateValue.Text=lang=="ko"?"약 25시간 59분":"About 25 h 59 min";
            EstimateHint.Text=lang=="ko"?"완료 예정 11/15 23:59 · 실제 작업 보정 · 구간 검증 포함":"Finishes around 11/15 23:59 · Job calibrated · Sample verification included";await Idle();UpdateLayout();
            foreach(var text in new[]{EstimateValue,EstimateHint}){
                var bounds=text.TransformToAncestor(SettingsPanel).TransformBounds(new Rect(text.RenderSize));
                Check(bounds.Right<=SettingsPanel.ActualWidth+1&&text.ActualWidth>0,$"Estimate text fits {text.Name} {size.Width} {lang}");
            }
            RefreshEstimateText();
            SettingsScroll.ScrollToTop();
        }
        Width=1180;Height=800;PreviewLanguage("ko");await Idle();
        EnglishButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));await Idle();
        Check(English&&AddButton.Content as string=="Add videos","Language switch selects English");
        KoreanButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));await Idle();
        Check(!English&&AddButton.Content as string=="영상 추가","Language switch selects Korean");
        Check(LanguageThumb.ActualWidth>20&&Math.Abs(LanguageThumb.ActualWidth*2-(LanguageSwitch.ActualWidth-6))<4,"Language switch halves");
        Check(MaximizeButton.Visibility==Visibility.Collapsed&&!MaximizeButton.IsEnabled,"Fixed window hides maximize");
        WindowState=WindowState.Maximized;await Idle();
        Check(WindowState==WindowState.Normal,"System maximize request preserves fixed mode");
        Check(ActualWidth<=SystemParameters.WorkArea.Width+2&&ActualHeight<=SystemParameters.WorkArea.Height+2,"Fixed window fits the monitor work area");
        // Plain encoder names and the RIFE model help.
        Check(EncoderName(new FrameDoubler.Core.EncoderCapability("h264_nvenc","h264","nvenc",true,false,null))=="NVENC (GPU)"
            &&EncoderName(new FrameDoubler.Core.EncoderCapability("hevc_amf","hevc","amf",true,true,null))=="AMF (GPU)"
            &&EncoderName(new FrameDoubler.Core.EncoderCapability("av1_qsv","av1","qsv",true,true,null))=="QSV (GPU)"
            &&EncoderName(new FrameDoubler.Core.EncoderCapability("libopenh264","h264","software",true,false,null))=="CPU","Encoder labels are plain (NVENC (GPU), CPU)");
        foreach(string lang in new[]{"ko","en"}){PreviewLanguage(lang);var help=(ModelHelp.ToolTip as string)?.Replace("⁠","")??"";
            Check(help.Contains("4.25")&&help.Contains("4.26")&&ModelHelp.Cursor==Cursors.Help,$"RIFE model help explains 4.25 and 4.26 {lang}");}
        PreviewLanguage("ko");
        Check(PresetBox.Items.Count==4,"Exactly four output presets");
        Select(PresetBox,"youtube-1080");await Idle();var mapped=CurrentOptions();
        Check(mapped.Platform=="youtube"&&mapped.OutputHeight==1080&&mapped.Codec=="h264"&&!mapped.QualityOnly,"YouTube 1080p uses recommended H264 bitrate and AAC");
        Select(PresetBox,"youtube-2160");await Idle();Check(CurrentOptions().OutputHeight==2160,"YouTube 4K output height");
        Select(PresetBox,"source");await Idle();var plain=CurrentOptions();
        Check(plain.MatchSource&&!plain.ScaleSourceBitrate&&plain.Platform is null&&plain.Codec is null&&plain.OutputHeight is null,"Source keeps bitrate and copies audio");
        Select(PresetBox,"source-scaled");await Idle();var scaled=CurrentOptions();
        Check(scaled.MatchSource&&scaled.ScaleSourceBitrate&&scaled.Platform is null,"Source scaled bitrate still copies audio");
        Select(PresetBox,"source");await Idle();
        // Queue order: numbers follow the list, dragging (MoveEntry) reorders, selection runs in list order.
        var first=new QueueEntry(@"C:\queue.mp4",T);var second=new QueueEntry(@"C:\queue.mp4",T);var third=new QueueEntry(@"C:\queue\c.mp4",T);
        Entries.Add(first);Entries.Add(second);Entries.Add(third);await Idle();
        Check(first.Number==1&&second.Number==2&&third.Number==3,"Queue entries are numbered in list order");
        MoveEntry(third,0);await Idle();
        Check(ReferenceEquals(Entries[0],third)&&third.Number==1&&first.Number==2&&second.Number==3,"Dragging an entry to the top renumbers the queue");
        QueueList.SelectedItems.Clear();QueueList.SelectedItems.Add(second);QueueList.SelectedItems.Add(third);await Idle();
        var picked=SelectedInListOrder();
        Check(picked.Length==2&&ReferenceEquals(picked[0],third)&&ReferenceEquals(picked[1],second)&&RemoveButton.IsEnabled,"Selected conversion keeps list order, not click order");
        RemoveButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));await Idle();
        Check(Entries.Count==1&&ReferenceEquals(Entries[0],first)&&first.Number==1,"Remove deletes every selected entry and renumbers");
        Entries.Clear();UpdateControls();await Idle();
        // A normal minimize stays in the taskbar; only close and Alt+F4 ask.
        var asked=new List<ExitTrigger>();var answer=DialogChoice.Cancel;ExitPrompt=trigger=>{asked.Add(trigger);return answer;};
        MinimizeButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));await Idle();
        Check(asked.Count==0&&IsVisible&&WindowState==WindowState.Minimized,"Default minimize stays in the taskbar without asking");
        Restore();await Idle();
        CloseButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));await Idle();
        Check(asked.Count==1&&asked[0]==ExitTrigger.Close&&IsVisible,"Close button asks and cancel keeps the window");
        Close();await Idle();
        Check(asked.Count==2&&asked[1]==ExitTrigger.Close&&IsVisible,"Alt+F4 / taskbar close also asks");
        SetupTray();ExitPrompt=null;ChangeSettings(p=>p with{MinimizeAction="tray"});
        MinimizeButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));await Idle();
        Check(!IsVisible&&tray?.Visible==true,"Only the explicit tray setting hides the minimized window");
        Restore();await Idle();Check(IsVisible&&WindowState==WindowState.Normal,"Tray restore");
        foreach(string lang in new[]{"ko","en"}){
            PreviewLanguage(lang);using var menu=CreateTrayMenu();
            var actions=menu.Items.OfType<System.Windows.Forms.ToolStripMenuItem>().ToArray();
            Check(actions.Length==5&&actions.All(x=>x.Image is not null),$"Tray actions all have matching icons {lang}");
            Check(menu.BackColor==BrandTrayMenu.Surface&&menu.ForeColor==BrandTrayMenu.Ink,$"Tray uses the main graphite and warm text palette {lang}");
            Check(actions.Select(x=>x.Name).SequenceEqual(new[]{"open","settings","updates","github","exit"}),$"Tray commands are grouped by function {lang}");
            menu.Translate(lang=="en");Check(menu.Items[0].Text==(lang=="en"?"Open window":"창 열기"),$"Tray labels match the application language {lang}");
        }
        PreviewLanguage("ko");ChangeSettings(p=>p with{MinimizeAction="taskbar"});
        Check(FrameDoubler.Core.Preferences.Load().MinimizeAction=="taskbar","Taskbar minimize preference is saved");
        WindowState=WindowState.Minimized;await Idle();Check(IsVisible,"Taskbar and system minimize do not implicitly hide to tray");Restore();
        await CheckSingleInstanceAsync(Check);DisposeTray();ExitPrompt=null;
        // Branded dialogs stay compact and every button fits, in both languages and for every trigger.
        foreach(string lang in new[]{"ko","en"}){
            PreviewLanguage(lang);
            foreach(var trigger in new[]{ExitTrigger.Close,ExitTrigger.Tray}){
                var dialog=CreateExitDialog(trigger);dialog.ShowActivated=false;dialog.Show();await Idle();dialog.UpdateLayout();
                var root=(FrameworkElement)dialog.Content;var choices=dialog.Choices;
                Check(choices.Count==2,$"Exit dialog has only cancel and one action {trigger} {lang}");
                foreach(var button in choices){
                    var bounds=button.TransformToAncestor(root).TransformBounds(new Rect(button.RenderSize));
                    // DesiredSize includes the button's left margin.
                    Check(bounds.Left>=0&&bounds.Right<=root.ActualWidth-20&&button.ActualWidth+button.Margin.Left+button.Margin.Right+.5>=button.DesiredSize.Width,$"Exit dialog button fits {button.Content} {trigger} {lang}");
                }
                Check(dialog.ActualWidth<=410&&dialog.ActualHeight<=220,$"Exit dialog is compact {trigger} {lang}");
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
            Check(settings.Navigation.Items.Count==7,$"Settings categories include capture and shortcuts {lang}");
            foreach(System.Windows.Controls.ListBoxItem item in settings.Navigation.Items){
                settings.Navigation.SelectedItem=item;await Idle();settings.UpdateLayout();
                var view=settings.Page!;string name=System.Windows.Automation.AutomationProperties.GetName(item);
                var controls=Tree(view).OfType<System.Windows.Controls.Control>().Where(x=>x is System.Windows.Controls.Button or System.Windows.Controls.ComboBox or System.Windows.Controls.CheckBox).ToList();
                Check(view.ActualHeight>0&&controls.Count>0,$"Settings page has controls {name} {lang}");
                if(controls.OfType<System.Windows.Controls.ComboBox>().FirstOrDefault(x=>System.Windows.Automation.AutomationProperties.GetAutomationId(x)=="SettingMinimizeAction") is {} minimize)
                    Check(minimize.Items.OfType<Choice>().Select(x=>x.Value).SequenceEqual(new[]{"taskbar","tray"}),$"Minimize offers only default taskbar or explicit tray {lang}");
                if(controls.OfType<System.Windows.Controls.ComboBox>().FirstOrDefault(x=>System.Windows.Automation.AutomationProperties.GetAutomationId(x)=="SettingAccelerator") is {} accelerator){
                    Check(accelerator.Items.OfType<Choice>().Select(x=>x.Value).SequenceEqual(new[]{"auto","vulkan","rtx"}),$"Acceleration offers auto, Vulkan and optional RTX {lang}");
                    accelerator.SelectedItem=accelerator.Items.OfType<Choice>().Single(x=>x.Value=="vulkan");await Idle();
                    Check(CurrentOptions().Accelerator=="vulkan"&&FrameDoubler.Core.Preferences.Load().Accelerator=="vulkan",$"Acceleration selection reaches conversion options and portable INI {lang}");
                    accelerator.SelectedItem=accelerator.Items.OfType<Choice>().Single(x=>x.Value=="auto");await Idle();
                }
                foreach(var control in controls){
                    var bounds=control.TransformToAncestor(view).TransformBounds(new Rect(control.RenderSize));
                    Check(bounds.Left>=0&&bounds.Right<=view.ActualWidth+1,$"Settings control fits {name} {control.GetType().Name} {lang}");
                }
            }
            settings.Select("advanced");await Idle();
            Check(AdvancedPanel.IsVisible&&System.Windows.Media.VisualTreeHelper.GetParent(AdvancedPanel) is not null&&!ReferenceEquals(AdvancedHost.Content,AdvancedPanel),$"Advanced controls appear in Settings {lang}");
            settings.Close();await Idle();
            Check(ReferenceEquals(AdvancedHost.Content,AdvancedPanel),$"Advanced controls return to the main window {lang}");
        }
        PreviewLanguage("ko");
        // A remembered close action skips the dialog and is saved to settings.ini.
        ChangeSettings(p=>p with{CloseAction="tray"});SetupTray();ExitPrompt=null;
        Check(FrameDoubler.Core.Preferences.Load().CloseAction=="tray","Close action setting is saved");
        CloseButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));await Idle();
        Check(!IsVisible,"Close action setting minimizes to the tray without asking");
        Restore();await Idle();ChangeSettings(p=>p with{CloseAction="ask"});DisposeTray();
        return new{Assertions=passed.Count,Passed=passed};
    }
}
