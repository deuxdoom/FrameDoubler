using System;
using System.Windows.Media;

namespace FrameDoubler.App;

// Embedded, unmodified font: consistent Latin, Hangul and Japanese without installation.
public static class UiFonts
{
    public static FontFamily Text {get;}=new(new Uri("pack://application:,,,/FrameDoubler;component/"),"./Assets/Fonts/#Pretendard JP Variable");
}
