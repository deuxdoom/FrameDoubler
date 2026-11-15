using System.Text.RegularExpressions;

namespace FrameDoubler.Core;

// Text that leaves the PC by the user's hand (diagnostics ZIP): the user name, the profile path
// and device instance IDs are replaced; vendor/product numbers stay for support.
public static class Privacy
{
    public static string Mask(string text,string user,string profile){
        if(profile.Length>0)text=text.Replace(profile,"%USERPROFILE%",StringComparison.OrdinalIgnoreCase);
        if(user.Length>1)text=Regex.Replace(text,@"(?<![\p{L}\p{N}])"+Regex.Escape(user)+@"(?![\p{L}\p{N}])","<user>",RegexOptions.IgnoreCase);
        text=Regex.Replace(text,@"(vid_[0-9a-f]{4}&pid_[0-9a-f]{4}[^#\\""]*)#[^#\\""]+",m=>m.Groups[1].Value+"#<instance>",RegexOptions.IgnoreCase);
        text=Regex.Replace(text,@"\{[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\}","{<id>}",RegexOptions.IgnoreCase);
        return text;
    }
}
