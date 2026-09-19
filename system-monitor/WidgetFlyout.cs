using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

sealed class WidgetFlyout : Form {
    public string Heading="",Subheading="",Footer="";
    public Func<Graphics,float> PaintBody;
    public Action HeaderAction=null;
    public Func<string> HeaderHint=null;
    public readonly int LogicalWidth,LogicalHeight;
    public bool Light=true;
    public Color Ink {get{return Light?Color.FromArgb(31,38,48):Color.FromArgb(238,242,248);}}
    public Color Muted {get{return Light?Color.FromArgb(108,116,129):Color.FromArgb(157,169,187);}}
    public Color CardColor {get{return Light?Color.White:Color.FromArgb(35,41,53);}}
    public Color Line {get{return Light?Color.FromArgb(225,229,236):Color.FromArgb(60,68,83);}}
    float scale=1,scroll,contentHeight;
    Form anchor;
    DateTime dismissed=DateTime.MinValue;
    int focusedButton=-1;
    delegate IntPtr MouseHook(int code,IntPtr message,IntPtr data);
    MouseHook outsideHookCallback;
    IntPtr outsideHook;
    readonly List<Tuple<RectangleF,Action>> hits=new List<Tuple<RectangleF,Action>>();
    readonly List<Tuple<RectangleF,string>> hints=new List<Tuple<RectangleF,string>>();
    readonly ToolTip tooltip=new ToolTip {InitialDelay=250,ReshowDelay=100};
    string currentHint="";
    const float BodyTop=48, BottomMargin=28;
    public WidgetFlyout(string title,int width,int height) {
        Text=title;LogicalWidth=width;LogicalHeight=height;
        FormBorderStyle=FormBorderStyle.None;ShowInTaskbar=false;StartPosition=FormStartPosition.Manual;TopMost=true;
        DoubleBuffered=true;KeyPreview=true;
        Deactivate+=delegate {Dismiss();};
        FormClosing+=delegate(object s,FormClosingEventArgs e){e.Cancel=true;Dismiss();};
    }
    protected override void OnVisibleChanged(EventArgs e){
        base.OnVisibleChanged(e);
        if(Visible){
            outsideHookCallback=delegate(int code,IntPtr message,IntPtr data){
                int kind=message.ToInt32();
                if(code>=0 && (kind==0x201 || kind==0x204)){
                    var point=new Point(Marshal.ReadInt32(data),Marshal.ReadInt32(data,4));
                    if(!Bounds.Contains(point) && (anchor==null || !anchor.Bounds.Contains(point)))BeginInvoke(new Action(Dismiss));
                }
                return CallNextHookEx(outsideHook,code,message,data);
            };
            outsideHook=SetWindowsHookEx(14,outsideHookCallback,GetModuleHandle(null),0);
            if(outsideHook==IntPtr.Zero)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),"Cannot monitor popup dismissal");
        }else if(outsideHook!=IntPtr.Zero){UnhookWindowsHookEx(outsideHook);outsideHook=IntPtr.Zero;}
    }
    protected override CreateParams CreateParams {get{var p=base.CreateParams;p.ClassStyle|=0x20000;return p;}}
    public void ToggleAt(Form source){
        if(Visible){Dismiss();return;}
        if((DateTime.UtcNow-dismissed).TotalMilliseconds<180)return;
        anchor=source;scale=GetDpiForWindow(source.Handle)/96f;if(scale<1)scale=1;
        using(var theme=Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
            Light=theme!=null && Convert.ToInt32(theme.GetValue("SystemUsesLightTheme",0))==1;
        BackColor=Light?Color.FromArgb(246,248,251):Color.FromArgb(24,29,39);
        var area=Screen.FromHandle(source.Handle).WorkingArea;
        var placement=TaskbarLayout.Flyout(source.Bounds,area,new Size((int)(LogicalWidth*scale),(int)(LogicalHeight*scale)),(int)(8*scale));
        // Fit narrow screens by scaling the complete layout, keeping controls reachable.
        scale=Math.Min(scale,placement.Width/(float)LogicalWidth);
        Bounds=placement;
        scroll=0;focusedButton=-1;Show();ShowWindow(Handle,5);Activate();
        int corner=2;DwmSetWindowAttribute(Handle,33,ref corner,4);Invalidate();
    }
    public void Dismiss(){if(!IsDisposed && Visible){dismissed=DateTime.UtcNow;Hide();}}
    protected override bool ProcessCmdKey(ref Message m,Keys key){
        if(key==Keys.Escape){Dismiss();return true;}
        if((key==Keys.Tab || key==(Keys.Shift|Keys.Tab)) && hits.Count>0){
            focusedButton=(focusedButton+(key==Keys.Tab?1:hits.Count-1)+hits.Count)%hits.Count;
            var target=hits[focusedButton].Item1;float viewport=Height/scale-BodyTop-BottomMargin;
            if(target.Top<scroll)scroll=target.Top;else if(target.Bottom>scroll+viewport)scroll=target.Bottom-viewport;
            Invalidate();return true;
        }
        if((key==Keys.Enter || key==Keys.Space) && focusedButton>=0 && focusedButton<hits.Count){hits[focusedButton].Item2();Invalidate();return true;}
        return base.ProcessCmdKey(ref m,key);
    }
    protected override void OnMouseWheel(MouseEventArgs e){scroll=Math.Max(0,Math.Min(Math.Max(0,contentHeight-(Height/scale-BodyTop-BottomMargin)),scroll-e.Delta/3f));Invalidate();base.OnMouseWheel(e);}
    protected override void OnMouseClick(MouseEventArgs e){if(e.Button!=MouseButtons.Left)return;if(e.Y/scale<BodyTop){if(HeaderAction!=null && e.X/scale>LogicalWidth-54)HeaderAction();return;}if(e.Y/scale>Height/scale-BottomMargin)return;var point=new PointF(e.X/scale,e.Y/scale-BodyTop+scroll);foreach(var hit in hits)if(hit.Item1.Contains(point)){hit.Item2();Invalidate();break;}base.OnMouseClick(e);}
    protected override void OnMouseMove(MouseEventArgs e){
        string text="";var point=new PointF(e.X/scale,e.Y/scale-BodyTop+scroll);
        if(e.Y/scale<BodyTop && e.X/scale>LogicalWidth-54 && HeaderHint!=null)text=HeaderHint();
        if(e.Y/scale>=BodyTop && e.Y/scale<Height/scale-BottomMargin)foreach(var hint in hints)if(hint.Item1.Contains(point)){text=hint.Item2;break;}
        if(text!=currentHint){currentHint=text;tooltip.SetToolTip(this,text);}base.OnMouseMove(e);
    }
    protected override void OnPaint(PaintEventArgs e){
        base.OnPaint(e);var g=e.Graphics;g.SmoothingMode=SmoothingMode.AntiAlias;g.TextRenderingHint=TextRenderingHint.AntiAliasGridFit;g.ScaleTransform(scale,scale);
        Label(g,Heading,20,16,LogicalWidth-40,14,Ink,true);
        if(HeaderAction!=null)Centered(g,"↻",LogicalWidth-52,9,32,23,Muted);
        hits.Clear();hints.Clear();var state=g.Save();g.SetClip(new RectangleF(16,BodyTop,LogicalWidth-32,Height/scale-BodyTop-BottomMargin));g.TranslateTransform(0,BodyTop-scroll);
        contentHeight=PaintBody!=null?PaintBody(g):0;
        if(focusedButton>=0 && focusedButton<hits.Count)using(var pen=new Pen(Color.FromArgb(54,126,185),1.5f))g.DrawRectangle(pen,Rectangle.Round(hits[focusedButton].Item1));
        g.Restore(state);
        float maxScroll=Math.Max(0,contentHeight-(Height/scale-BodyTop-BottomMargin));if(scroll>maxScroll){scroll=maxScroll;Invalidate();}
        if(contentHeight>Height/scale-BodyTop-BottomMargin){float h=Height/scale-BodyTop-BottomMargin;float thumb=Math.Max(20,h*h/contentHeight);float y=BodyTop+(h-thumb)*scroll/(contentHeight-h);using(var brush=new SolidBrush(Color.FromArgb(90,Muted)))g.FillRectangle(brush,LogicalWidth-7,y,3,thumb);}
        Label(g,Footer,24,Height/scale-22,LogicalWidth-48,10,Muted);
    }
    public void Label(Graphics g,string text,float x,float y,float width,float size,Color color,bool bold=false,bool right=false){
        using(var font=new Font("Segoe UI",size,bold?FontStyle.Bold:FontStyle.Regular,GraphicsUnit.Pixel))
        using(var brush=new SolidBrush(color))
        using(var format=new StringFormat{Trimming=StringTrimming.EllipsisCharacter,FormatFlags=StringFormatFlags.NoWrap,Alignment=right?StringAlignment.Far:StringAlignment.Near})
            g.DrawString(text,font,brush,new RectangleF(x,y,width,size*1.6f),format);
    }
    public void Card(Graphics g,float x,float y,float w,float h){using(var p=TaskbarSurface.Rounded(new RectangleF(x,y,w,h),10))using(var b=new SolidBrush(CardColor))using(var pen=new Pen(Line)){g.FillPath(b,p);g.DrawPath(pen,p);}}
    public void Hint(RectangleF bounds,string text){hints.Add(Tuple.Create(bounds,text));}
    public float Wrapped(Graphics g,string text,float x,float y,float width,float size,Color color){
        if(string.IsNullOrEmpty(text))return 0;
        using(var font=new Font("Segoe UI",size,GraphicsUnit.Pixel))using(var brush=new SolidBrush(color)){
            float height=(float)Math.Ceiling(g.MeasureString(text,font,(int)width).Height)+3;
            g.DrawString(text,font,brush,new RectangleF(x,y,width,height));return height;
        }
    }
    public void Centered(Graphics g,string text,float x,float y,float width,float size,Color color,bool bold=false){
        using(var font=new Font("Segoe UI",size,bold?FontStyle.Bold:FontStyle.Regular,GraphicsUnit.Pixel))using(var brush=new SolidBrush(color))
        using(var format=new StringFormat {Alignment=StringAlignment.Center})g.DrawString(text,font,brush,new RectangleF(x,y,width,size*1.6f),format);
    }
    public void Bar(Graphics g,float x,float y,float width,double value,Color color){
        using(var p=TaskbarSurface.Rounded(new RectangleF(x,y,width,5),2.5f))using(var b=new SolidBrush(Line))g.FillPath(b,p);
        float fill=(float)(width*Math.Max(0,Math.Min(100,value))/100);if(fill>=5)using(var p=TaskbarSurface.Rounded(new RectangleF(x,y,fill,5),2.5f))using(var b=new SolidBrush(color))g.FillPath(b,p);
    }
    public void Button(Graphics g,string text,RectangleF bounds,Action action,bool selected=false){
        using(var p=TaskbarSurface.Rounded(bounds,6))using(var b=new SolidBrush(selected?(Light?Color.FromArgb(223,234,245):Color.FromArgb(53,70,89)):CardColor))g.FillPath(b,p);
        using(var font=new Font("Segoe UI",11,selected?FontStyle.Bold:FontStyle.Regular,GraphicsUnit.Pixel))
        using(var brush=new SolidBrush(selected?Color.FromArgb(54,126,185):Muted))
        using(var format=new StringFormat{Alignment=StringAlignment.Center,LineAlignment=StringAlignment.Center})g.DrawString(text,font,brush,bounds,format);
        hits.Add(Tuple.Create(bounds,action));
    }
    protected override void Dispose(bool disposing){if(disposing){tooltip.Dispose();if(outsideHook!=IntPtr.Zero){UnhookWindowsHookEx(outsideHook);outsideHook=IntPtr.Zero;}}base.Dispose(disposing);}
    [DllImport("user32.dll",SetLastError=true)] static extern IntPtr SetWindowsHookEx(int id,MouseHook callback,IntPtr module,uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook,int code,IntPtr message,IntPtr data);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h,int n);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h,int attribute,ref int value,int size);
}
