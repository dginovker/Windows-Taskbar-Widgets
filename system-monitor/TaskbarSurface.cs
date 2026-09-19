using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.IO;
using System.Collections.Generic;
using System.Text;
using System.Web.Script.Serialization;

// A per-pixel alpha surface: the real taskbar remains visible between the glyphs.
sealed class TaskbarSurface : Form {
    public int Offset, LogicalWidth;
    public bool EnabledOnTaskbar=true;
    public Action<Graphics,bool> PaintContent;
    public Action Shutdown;
    public string Hint="";
    readonly Timer pulse=new Timer {Interval=500};
    readonly ToolTip tip=new ToolTip {InitialDelay=400,ReshowDelay=100};
    bool hover;
    string monitor="";
    readonly string preferencesPath;
    public sealed class Placement {public int Offset;public string Monitor="";}

    public TaskbarSurface(string title,string appId,int offset,int width) {
        preferencesPath=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),appId,"placement.json");
        if(File.Exists(preferencesPath)){
            var saved=new JavaScriptSerializer().Deserialize<Placement>(File.ReadAllText(preferencesPath));
            if(saved==null || saved.Offset<0)throw new InvalidDataException("Invalid widget placement: "+preferencesPath);
            offset=saved.Offset;monitor=saved.Monitor??"";
        }
        Text=title;Offset=offset;LogicalWidth=width;FormBorderStyle=FormBorderStyle.None;
        ShowInTaskbar=false;TopMost=true;StartPosition=FormStartPosition.Manual;Cursor=Cursors.Hand;
        // Keep a control window even when hidden, so installers can request a clean exit.
        var controlHandle=Handle;
        pulse.Tick+=delegate {RefreshSurface();};pulse.Start();
        MouseEnter+=delegate {hover=true;tip.SetToolTip(this,Hint);RefreshSurface();};
        MouseLeave+=delegate {hover=false;RefreshSurface();};
    }
    protected override bool ShowWithoutActivation {get{return true;}}
    protected override CreateParams CreateParams {get{var p=base.CreateParams;p.ExStyle|=0x80000|0x80|0x08000000|0x8;return p;}}
    protected override void WndProc(ref Message m){if(m.Msg==0x8031){if(Shutdown!=null)Shutdown();return;}base.WndProc(ref m);}
    public static Color Foreground(bool light){return light?Color.FromArgb(36,40,47):Color.FromArgb(242,244,249);}
    public static Color Secondary(bool light){return light?Color.FromArgb(95,101,110):Color.FromArgb(176,184,198);}
    public static void TextAt(Graphics g,string text,float x,float y,float size,Color color,bool bold=false){
        using(var font=new Font("Segoe UI",size,bold?FontStyle.Bold:FontStyle.Regular,GraphicsUnit.Pixel))
        using(var brush=new SolidBrush(color)) g.DrawString(text,font,brush,x,y,StringFormat.GenericTypographic);
    }
    public static GraphicsPath Rounded(RectangleF r,float radius){var p=new GraphicsPath();float d=radius*2;p.AddArc(r.X,r.Y,d,d,180,90);p.AddArc(r.Right-d,r.Y,d,d,270,90);p.AddArc(r.Right-d,r.Bottom-d,d,d,0,90);p.AddArc(r.X,r.Bottom-d,d,d,90,90);p.CloseFigure();return p;}
    public void RefreshSurface(){
        if(IsDisposed || PaintContent==null)return;
        IntPtr taskbar=FindTaskbar();Rect bar,front;
        if(!EnabledOnTaskbar || taskbar==IntPtr.Zero || !IsWindowVisible(taskbar) || !GetWindowRect(taskbar,out bar)){Hide();return;}
        var screen=Screen.FromHandle(taskbar).Bounds;var fg=GetForegroundWindow();
        bool fullscreen=fg!=taskbar && fg!=Handle && fg!=FindWindow("Progman",null) && fg!=FindWindow("WorkerW",null)
            && GetWindowRect(fg,out front) && front.Left<=screen.Left && front.Top<=screen.Top && front.Right>=screen.Right && front.Bottom>=screen.Bottom;
        float scale=GetDpiForWindow(taskbar)/96f;if(scale<=0)scale=1;
        Rectangle placement;
        if(fullscreen || !TaskbarLayout.TryStrip(Rectangle.FromLTRB(bar.Left,bar.Top,bar.Right,bar.Bottom),screen,scale,LogicalWidth,Offset,out placement)){Hide();return;}
        int height=placement.Height,width=placement.Width;
        if(!Visible)Show();
        bool light=false;
        using(var theme=Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
            light=theme!=null && Convert.ToInt32(theme.GetValue("SystemUsesLightTheme",0))==1;
        using(var bitmap=new Bitmap(width,height,PixelFormat.Format32bppArgb)) {
            using(var g=Graphics.FromImage(bitmap)) {
                g.Clear(Color.FromArgb(1,0,0,0));g.SmoothingMode=SmoothingMode.AntiAlias;g.TextRenderingHint=TextRenderingHint.AntiAliasGridFit;
                g.ScaleTransform(scale,scale);g.TranslateTransform(0,(height/scale-48)/2);
                if(hover)using(var path=Rounded(new RectangleF(0,4,LogicalWidth,40),6))using(var brush=new SolidBrush(Color.FromArgb(light?18:25,light?Color.Black:Color.White)))g.FillPath(brush,path);
                PaintContent(g,light);
            }
            Present(bitmap,placement.Left,placement.Top);
        }
        ShowWindow(Handle,4);
        SetWindowPos(Handle,new IntPtr(-1),0,0,0,0,0x53);
    }
    IntPtr FindTaskbar(){
        if(monitor.Length==0)return FindWindow("Shell_TrayWnd",null);
        IntPtr selected=IntPtr.Zero;
        EnumWindows(delegate(IntPtr window,IntPtr unused){
            var name=new StringBuilder(128);GetClassName(window,name,name.Capacity);
            if((name.ToString()=="Shell_TrayWnd" || name.ToString()=="Shell_SecondaryTrayWnd") && Screen.FromHandle(window).DeviceName==monitor)selected=window;
            return true;
        },IntPtr.Zero);
        return selected;
    }
    public void AddPlacementMenu(ContextMenuStrip menu){menu.Items.Add("Position and monitor...",null,delegate {ConfigurePlacement();});}
    void ConfigurePlacement(){
        using(var dialog=new Form {Text="Widget placement",ClientSize=new Size(370,175),FormBorderStyle=FormBorderStyle.FixedDialog,MaximizeBox=false,MinimizeBox=false,StartPosition=FormStartPosition.CenterScreen,Font=new Font("Segoe UI",10)}){
            var monitors=new ComboBox {Left=145,Top=20,Width=200,DropDownStyle=ComboBoxStyle.DropDownList};
            monitors.Items.Add("Primary monitor");var devices=Screen.AllScreens;
            foreach(var device in devices)monitors.Items.Add(device.DeviceName);
            monitors.SelectedIndex=0;for(int i=0;i<devices.Length;i++)if(devices[i].DeviceName==monitor)monitors.SelectedIndex=i+1;
            var offset=new NumericUpDown {Left=145,Top=65,Width=200,Minimum=0,Maximum=20000,Value=Math.Min(20000,Offset)};
            var apply=new Button {Text="Apply",Left=255,Top=120,Width=90,DialogResult=DialogResult.OK};
            dialog.Controls.AddRange(new Control[]{new Label{Text="Monitor",Left=20,Top=24,Width=120},monitors,new Label{Text="Offset from left",Left=20,Top=69,Width=125},offset,apply});
            dialog.AcceptButton=apply;
            if(dialog.ShowDialog()!=DialogResult.OK)return;
            monitor=monitors.SelectedIndex==0?"":devices[monitors.SelectedIndex-1].DeviceName;Offset=(int)offset.Value;
            Directory.CreateDirectory(Path.GetDirectoryName(preferencesPath));
            File.WriteAllText(preferencesPath,new JavaScriptSerializer().Serialize(new Placement{Monitor=monitor,Offset=Offset}));
            RefreshSurface();
        }
    }
    void Present(Bitmap bitmap,int x,int y){
        IntPtr screen=GetDC(IntPtr.Zero),dc=CreateCompatibleDC(screen),image=bitmap.GetHbitmap(Color.FromArgb(0)),old=SelectObject(dc,image);
        try{var target=new Point(x,y);var source=new Point(0,0);var size=bitmap.Size;var blend=new Blend {Operation=0,Flags=0,Alpha=255,Format=1};
            if(!UpdateLayeredWindow(Handle,screen,ref target,ref size,dc,ref source,0,ref blend,2))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }finally{SelectObject(dc,old);DeleteObject(image);DeleteDC(dc);ReleaseDC(IntPtr.Zero,screen);}
    }
    protected override void Dispose(bool disposing){if(disposing){pulse.Dispose();tip.Dispose();}base.Dispose(disposing);}
    [StructLayout(LayoutKind.Sequential)] struct Rect{public int Left,Top,Right,Bottom;}
    [StructLayout(LayoutKind.Sequential,Pack=1)] struct Blend{public byte Operation,Flags,Alpha,Format;}
    delegate bool EnumWindowCallback(IntPtr window,IntPtr parameter);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowCallback callback,IntPtr parameter);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr window,StringBuilder name,int length);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern IntPtr FindWindow(string c,string t);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h,out Rect r);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h,int n);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h,IntPtr a,int x,int y,int w,int ht,uint f);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h,IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc,IntPtr o);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("user32.dll",SetLastError=true)] static extern bool UpdateLayeredWindow(IntPtr h,IntPtr screen,ref Point dst,ref Size size,IntPtr src,ref Point pt,uint key,ref Blend blend,uint flags);
}

internal static class TaskbarLayout {
    public static bool TryStrip(Rectangle bar,Rectangle screen,float scale,int logicalWidth,int offset,out Rectangle result){
        result=Rectangle.Empty;
        if(scale<=0 || logicalWidth<=0 || bar.Width<bar.Height || bar.Height<24*scale)return false;
        var visible=Rectangle.Intersect(bar,screen);
        if(visible.Height<bar.Height-2 || visible.Width<bar.Width-2)return false;
        int width=(int)Math.Ceiling(logicalWidth*scale);
        if(width>bar.Width)return false;
        int left=Math.Max(bar.Left,Math.Min(bar.Right-width,bar.Left+(int)(Math.Max(0,offset)*scale)));
        result=new Rectangle(left,bar.Top,width,bar.Height);return true;
    }
    public static Rectangle Flyout(Rectangle anchor,Rectangle work,Size requested,int gap){
        int width=Math.Min(requested.Width,Math.Max(1,work.Width-16));
        int height=Math.Min(requested.Height,Math.Max(1,work.Height-16));
        int x=Math.Max(work.Left+8,Math.Min(work.Right-width-8,anchor.Left));
        int y=anchor.Top>=work.Bottom?anchor.Top-height-gap:anchor.Bottom+gap;
        y=Math.Max(work.Top+8,Math.Min(work.Bottom-height-8,y));return new Rectangle(x,y,width,height);
    }
}
