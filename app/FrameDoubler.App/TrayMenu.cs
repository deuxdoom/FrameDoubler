using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using Forms=System.Windows.Forms;

namespace FrameDoubler.App;

internal sealed class BrandTrayMenu:Forms.ContextMenuStrip
{
    internal static readonly Color Surface=Color.FromArgb(29,35,39),Ink=Color.FromArgb(243,241,235),
        Muted=Color.FromArgb(166,177,184),Accent=Color.FromArgb(255,122,76);
    private readonly List<Image> images=[];
    private readonly Font menuFont=new("Segoe UI",10);
    internal BrandTrayMenu(){
        BackColor=Surface;ForeColor=Ink;Font=menuFont;Renderer=new BrandRenderer();
        ShowImageMargin=true;ShowCheckMargin=false;ImageScalingSize=new Size(20,20);
        Padding=new Forms.Padding(6);ShowItemToolTips=false;
        Opened+=(_,_)=>WindowFrame.RoundCorners(Handle);
    }
    internal Forms.ToolStripMenuItem Add(string key,string text,string glyph,EventHandler clicked){
        var icon=Glyph(glyph);images.Add(icon);
        var item=new Forms.ToolStripMenuItem(text,icon,clicked){Name=key,Tag=key,AutoSize=false,Size=new Size(240,36),Padding=new Forms.Padding(8,6,12,6)};
        Items.Add(item);return item;
    }
    internal void Translate(bool english){
        foreach(Forms.ToolStripItem item in Items)item.Text=item.Name switch{
            "open"=>english?"Open window":"창 열기","settings"=>english?"Settings":"설정",
            "updates"=>english?"Check for updates":"업데이트 확인","github"=>"GitHub","exit"=>english?"Exit":"종료",_=>item.Text};
    }
    private static Bitmap Glyph(string glyph){
        var bitmap=new Bitmap(32,32,PixelFormat.Format32bppArgb);
        using var graphics=Graphics.FromImage(bitmap);graphics.Clear(Color.Transparent);graphics.TextRenderingHint=TextRenderingHint.AntiAliasGridFit;
        using var font=new Font("Segoe Fluent Icons",23,FontStyle.Regular,GraphicsUnit.Pixel);
        using var brush=new SolidBrush(Muted);using var format=new StringFormat{Alignment=StringAlignment.Center,LineAlignment=StringAlignment.Center};
        graphics.DrawString(glyph,font,brush,new RectangleF(0,0,32,32),format);return bitmap;
    }
    protected override void Dispose(bool disposing){if(disposing){foreach(var image in images)image.Dispose();images.Clear();menuFont.Dispose();}base.Dispose(disposing);}
    private sealed class BrandRenderer:Forms.ToolStripProfessionalRenderer
    {
        internal BrandRenderer(){RoundedEdges=false;}
        private static Color Highlight(Forms.ToolStripItem item)=>item.Name=="exit"?Color.FromArgb(255,142,137):Accent;
        protected override void OnRenderToolStripBackground(Forms.ToolStripRenderEventArgs e)=>e.Graphics.Clear(Surface);
        protected override void OnRenderToolStripBorder(Forms.ToolStripRenderEventArgs e){using var pen=new Pen(Color.FromArgb(69,83,92));e.Graphics.DrawRectangle(pen,0,0,e.ToolStrip.Width-1,e.ToolStrip.Height-1);}
        protected override void OnRenderImageMargin(Forms.ToolStripRenderEventArgs e){}
        protected override void OnRenderMenuItemBackground(Forms.ToolStripItemRenderEventArgs e){
            if(!e.Item.Selected&&!e.Item.Pressed)return;
            using var fill=new SolidBrush(Color.FromArgb(41,51,58));e.Graphics.FillRectangle(fill,2,1,e.Item.Width-4,e.Item.Height-2);
            using var accent=new SolidBrush(Highlight(e.Item));e.Graphics.FillRectangle(accent,2,7,2,e.Item.Height-14);
        }
        protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e){e.TextColor=!e.Item.Enabled?Muted:e.Item.Selected?Highlight(e.Item):Ink;base.OnRenderItemText(e);}
        protected override void OnRenderSeparator(Forms.ToolStripSeparatorRenderEventArgs e){using var pen=new Pen(Color.FromArgb(48,55,60));e.Graphics.DrawLine(pen,10,e.Item.Height/2,e.Item.Width-10,e.Item.Height/2);}
        protected override void OnRenderItemImage(Forms.ToolStripItemImageRenderEventArgs e){
            if(e.Image is null)return;
            var color=e.Item.Selected?Highlight(e.Item):e.Item.Name=="open"?Accent:Muted;
            using var attributes=new ImageAttributes();attributes.SetColorMatrix(new ColorMatrix([
                [0,0,0,0,0],[0,0,0,0,0],[0,0,0,0,0],[0,0,0,1,0],[color.R/255f,color.G/255f,color.B/255f,0,1]]));
            e.Graphics.InterpolationMode=InterpolationMode.HighQualityBicubic;
            e.Graphics.DrawImage(e.Image,e.ImageRectangle,0,0,e.Image.Width,e.Image.Height,GraphicsUnit.Pixel,attributes);
        }
    }
}

public partial class MainWindow
{
    internal BrandTrayMenu CreateTrayMenu(){
        var menu=new BrandTrayMenu();
        menu.Add("open",T("창 열기","Open window"),"\uE8A7",(_,_)=>Restore());
        menu.Add("settings",T("설정","Settings"),"\uE713",(_,_)=>{Restore();if(!busy)new SettingsWindow(this).ShowDialog();});
        menu.Add("updates",T("업데이트 확인","Check for updates"),"\uE895",async(_,_)=>{Restore();await ShowUpdatesAsync();});
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Add("github","GitHub","\uE943",(_,_)=>Open("https://github.com/deuxdoom/FrameDoubler"));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Add("exit",T("종료","Exit"),"\uE8BB",(_,_)=>RequestExit(ExitTrigger.Tray));
        return menu;
    }
    private void DisposeTray(){
        if(tray is null)return;
        tray.Visible=false;var menu=tray.ContextMenuStrip;var icon=tray.Icon;tray.Dispose();tray=null;menu?.Dispose();icon?.Dispose();
    }
}
