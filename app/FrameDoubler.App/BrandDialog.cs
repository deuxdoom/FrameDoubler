using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;

namespace FrameDoubler.App;

public enum DialogChoice{Cancel,Primary,Secondary}

// WPF wraps Korean between any two syllables. Word joiners (U+2060) between adjacent characters
// of a Hangul word keep line breaks at spaces only, like CSS word-break: keep-all on the site.
internal static class Hangul
{
    private static bool Is(char c)=>c is >='가' and <='힣' or >='㄰' and <='㆏' or >='ᄀ' and <='ᇿ';
    public static string KeepWords(string text){
        if(string.IsNullOrEmpty(text))return text;
        var result=new System.Text.StringBuilder(text.Length+16);
        for(int i=0;i<text.Length;i++){
            if(i>0&&!char.IsWhiteSpace(text[i-1])&&!char.IsWhiteSpace(text[i])&&(Is(text[i-1])||Is(text[i]))&&text[i-1]!='⁠')result.Append('⁠');
            result.Append(text[i]);
        }
        return result.ToString();
    }
}
public sealed record DialogButton(string Text,DialogChoice Choice,string Style,bool IsDefault=false);

// Compact frameless dialog in the app's graphite and signal-orange identity, used for
// exit/tray confirmation and short notices such as "you are up to date".
public sealed class BrandDialog:Window
{
    public static class Glyphs{public const string Exit="",Tray="",Update="",Done="",Error="";}
    private readonly CancellationTokenSource closed=new();
    private readonly TextBlock glyph=new(){FontFamily=new FontFamily("Segoe Fluent Icons"),FontSize=20,Foreground=Brush("#FF8C60"),HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center};
    private readonly TextBlock heading=new(){FontSize=17,FontWeight=FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap};
    private readonly TextBlock body=new(){Foreground=Brush("#A6B1B8"),Margin=new Thickness(0,8,0,0),TextWrapping=TextWrapping.Wrap,LineHeight=20};
    private readonly TextBlock notice=new(){Foreground=Brush("#FFB08C"),TextWrapping=TextWrapping.Wrap,LineHeight=19};
    private readonly Border noticeBox=new(){Background=Brush("#2A211B"),BorderBrush=Brush("#5A3A2A"),BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(8),Padding=new Thickness(12,9,12,9),Margin=new Thickness(0,14,0,0)};
    private readonly StackPanel buttons=new(){Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,24,0,0)};
    public DialogChoice Choice{get;private set;}=DialogChoice.Cancel;
    internal System.Collections.Generic.IReadOnlyList<Button> Choices=>System.Linq.Enumerable.ToList(System.Linq.Enumerable.OfType<Button>(buttons.Children));
    public CancellationToken ClosedToken=>closed.Token;
    public BrandDialog(Window? owner,string title){
        Title=title;Width=460;SizeToContent=SizeToContent.Height;ResizeMode=ResizeMode.NoResize;
        Style=(Style)FindResource(typeof(Window));WindowStyle=WindowStyle.None;
        // A hidden owner (window in the tray) would keep the dialog invisible, so centre it on screen instead.
        if(owner is {IsVisible:true,WindowState:not WindowState.Minimized}){Owner=owner;WindowStartupLocation=WindowStartupLocation.CenterOwner;ShowInTaskbar=false;}
        else{WindowStartupLocation=WindowStartupLocation.CenterScreen;Topmost=true;}
        WindowChrome.SetWindowChrome(this,new WindowChrome{CaptionHeight=40,ResizeBorderThickness=new Thickness(0),GlassFrameThickness=new Thickness(0),CornerRadius=new CornerRadius(12),UseAeroCaptionButtons=false});
        SourceInitialized+=(_,_)=>WindowFrame.RoundCorners(this);
        Closed+=(_,_)=>closed.Cancel();
        PreviewKeyDown+=(_,e)=>{if(e.Key==Key.Escape){Choice=DialogChoice.Cancel;Close();e.Handled=true;}};

        var caption=new Grid{Height=40};
        var name=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(16,0,0,0),VerticalAlignment=VerticalAlignment.Center};
        name.Children.Add(new Image{Source=new BitmapImage(new Uri("pack://application:,,,/Assets/icon.png")),Width=16,Height=16,Margin=new Thickness(0,0,8,0)});
        name.Children.Add(new TextBlock{Text=title,Foreground=Brush("#A6B1B8"),FontSize=12,VerticalAlignment=VerticalAlignment.Center});
        caption.Children.Add(name);
        var close=new Button{Content="",FontFamily=new FontFamily("Segoe Fluent Icons"),Style=(Style)FindResource("CloseCaptionButton"),HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,0,4,0),ToolTip=title};
        System.Windows.Automation.AutomationProperties.SetAutomationId(close,"DialogClose");
        WindowChrome.SetIsHitTestVisibleInChrome(close,true);close.Click+=(_,_)=>{Choice=DialogChoice.Cancel;Close();};caption.Children.Add(close);

        var content=new Grid{Margin=new Thickness(24,8,24,22)};
        content.ColumnDefinitions.Add(new(){Width=GridLength.Auto});content.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});
        var badge=new Border{Width=44,Height=44,CornerRadius=new CornerRadius(13),Background=Brush("#30271F"),VerticalAlignment=VerticalAlignment.Top,Margin=new Thickness(0,0,16,0),Child=glyph};
        content.Children.Add(badge);
        var text=new StackPanel();text.Children.Add(heading);text.Children.Add(body);noticeBox.Child=notice;text.Children.Add(noticeBox);text.Children.Add(buttons);
        Grid.SetColumn(text,1);content.Children.Add(text);

        var layout=new StackPanel();layout.Children.Add(caption);layout.Children.Add(content);
        Content=new Border{Background=Background,BorderBrush=Brush("#30373C"),BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(12),Child=layout};
    }
    // Content can be replaced while the dialog is open, e.g. "checking" -> "up to date".
    public void Configure(string icon,string title,string message,string? warning,params DialogButton[] choices){
        glyph.Text=icon;heading.Text=Hangul.KeepWords(title);body.Text=Hangul.KeepWords(message);body.Visibility=string.IsNullOrEmpty(message)?Visibility.Collapsed:Visibility.Visible;
        notice.Text=Hangul.KeepWords(warning??"");noticeBox.Visibility=string.IsNullOrEmpty(warning)?Visibility.Collapsed:Visibility.Visible;
        buttons.Children.Clear();
        foreach(var choice in choices){
            var button=new Button{Content=choice.Text,Style=(Style)FindResource(choice.Style),IsDefault=choice.IsDefault,MinWidth=choice.Style=="TextButton"?0:104,
                Margin=new Thickness(buttons.Children.Count==0?0:10,0,0,0),Padding=choice.Style=="TextButton"?new Thickness(12,5,12,5):new Thickness(16,8,16,8)};
            System.Windows.Automation.AutomationProperties.SetAutomationId(button,"Dialog"+choice.Choice);
            button.Click+=(_,_)=>{Choice=choice.Choice;Close();};
            buttons.Children.Add(button);
            if(choice.IsDefault){if(IsLoaded)button.Focus();else Loaded+=(_,_)=>button.Focus();}
        }
    }
    private static SolidColorBrush Brush(string value)=>(SolidColorBrush)new BrushConverter().ConvertFromString(value)!;
}
