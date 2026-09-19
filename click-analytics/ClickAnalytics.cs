using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Threading;
using System.Globalization;
using System.Web.Script.Serialization;
using System.Windows.Forms;

public sealed class CountStore {
    public Dictionary<string, Dictionary<string,double>> Hours = new Dictionary<string, Dictionary<string,double>>();
    public void Add(string metric, double amount) {
        string hour = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:00:00Z",CultureInfo.InvariantCulture);
        if (!Hours.ContainsKey(hour)) Hours[hour] = new Dictionary<string,double>();
        double old; Hours[hour].TryGetValue(metric,out old); Hours[hour][metric] = old + amount;
    }
    public double Total(string metric, bool today) {
        return Hours.Where(h => !today || DateTime.Parse(h.Key,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind).ToLocalTime().Date == DateTime.Today).Sum(h => Value(h.Value,metric));
    }
    public static double Value(Dictionary<string,double> values,string metric) {double n;return values.TryGetValue(metric,out n)?n:0;}
    public void Save(string path) {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        string temp=path+".tmp"; File.WriteAllText(temp,new JavaScriptSerializer {MaxJsonLength=int.MaxValue}.Serialize(this));
        if (File.Exists(path)) File.Replace(temp,path,path+".bak"); else File.Move(temp,path);
    }
    public static CountStore Load(string path) {
        if(!File.Exists(path))return new CountStore();
        var result=new JavaScriptSerializer {MaxJsonLength=int.MaxValue}.Deserialize<CountStore>(File.ReadAllText(path));
        if(result==null || result.Hours==null)throw new InvalidDataException("Invalid click history: "+path);
        foreach(var hour in result.Hours){
            DateTime timestamp;
            if(!DateTime.TryParseExact(hour.Key,"yyyy-MM-dd'T'HH':00:00Z'",CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal,out timestamp) || hour.Value==null)
                throw new InvalidDataException("Invalid hourly bucket: "+path);
            foreach(var value in hour.Value.Values)if(double.IsNaN(value)||double.IsInfinity(value)||value<0)throw new InvalidDataException("Invalid count: "+path);
        }
        return result;
    }
}

static class Program {
    [STAThread] static void Main(string[] args) {
        bool created;
        using (var mutex = new Mutex(true,"Local\\ClickAnalytics.Windows",out created)) {
            if (!created) return;
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            try { using(var app=new CounterApp()) Application.Run(app); }
            catch(Exception e) { MessageBox.Show(e.Message,"Click Analytics could not start"); }
        }
    }

}

sealed class CounterApp : ApplicationContext {
    delegate IntPtr Hook(int code,IntPtr message,IntPtr data);
    [StructLayout(LayoutKind.Sequential)] struct MouseData {public int X,Y; public uint Data,Flags,Time;public UIntPtr Extra;}
    [DllImport("user32.dll",SetLastError=true)] static extern IntPtr SetWindowsHookEx(int id,Hook callback,IntPtr module,uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook,int code,IntPtr message,IntPtr data);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);
    [DllImport("iphlpapi.dll")] static extern int GetBestInterface(uint destination,out uint index);
    readonly Hook mouseCallback,keyboardCallback;
    IntPtr mouseHook,keyboardHook;
    readonly bool[] down=new bool[256];
    Point? previous;
    readonly CountStore store;
    readonly string path=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ClickAnalytics","counts.json");
    readonly TaskbarSurface compact=new TaskbarSurface("Mouse & keyboard counts","ClickAnalytics",12,104);
    readonly WidgetFlyout details=new WidgetFlyout("Click Analytics",460,550);

    readonly NotifyIcon tray=new NotifyIcon();
    readonly System.Windows.Forms.Timer timer=new System.Windows.Forms.Timer();
    string network="Waiting for network sample",networkId="",saveError="";
    long previousRx,previousTx;
    int ticks;
    bool counterEnabled=true;

    public CounterApp() {
        store=CountStore.Load(path);
        compact.PaintContent=PaintCounter;
        compact.Shutdown=delegate {ExitThread();};
        compact.MouseClick+=delegate(object sender,MouseEventArgs e){if(e.Button==MouseButtons.Left)ShowDetails();};
        details.Heading="Totals";details.Subheading="Mouse, keyboard and network";details.PaintBody=PaintActivity;
        var menu=new ContextMenuStrip();menu.Items.Add("Show totals and history",null,delegate {ShowDetails();});
        menu.Items.Add("Show/hide taskbar counts",null,delegate {counterEnabled=!counterEnabled;DockCounter();});
        menu.Items.Add("Exit",null,delegate {ExitThread();});
        compact.ContextMenuStrip=menu;compact.AddPlacementMenu(menu);
        tray.Icon=SystemIcons.Information;tray.ContextMenuStrip=menu;tray.Visible=true;
        tray.MouseClick+=delegate(object sender,MouseEventArgs e){if(e.Button==MouseButtons.Left)ShowDetails();};
        mouseCallback=MouseEvent;keyboardCallback=KeyboardEvent;
        mouseHook=SetWindowsHookEx(14,mouseCallback,GetModuleHandle(null),0);
        keyboardHook=SetWindowsHookEx(13,keyboardCallback,GetModuleHandle(null),0);
        if(mouseHook==IntPtr.Zero || keyboardHook==IntPtr.Zero) {
            int error=Marshal.GetLastWin32Error();if(mouseHook!=IntPtr.Zero)UnhookWindowsHookEx(mouseHook);if(keyboardHook!=IntPtr.Zero)UnhookWindowsHookEx(keyboardHook);
            tray.Dispose();throw new Win32Exception(error,"Windows input monitoring could not start");
        }
        timer.Interval=1000;timer.Tick+=delegate {SampleNetwork();UpdateDisplay();DockCounter();if(++ticks%10==0)Save();};timer.Start();
        Microsoft.Win32.SystemEvents.SessionEnding+=SessionEnding;
        UpdateDisplay();ShowCompact();Save();
    }
    void SessionEnding(object sender,Microsoft.Win32.SessionEndingEventArgs e){Save();}
    IntPtr MouseEvent(int code,IntPtr message,IntPtr data) {
        if(code>=0) {
            var m=(MouseData)Marshal.PtrToStructure(data,typeof(MouseData));int kind=message.ToInt32();
            if(kind==0x201)store.Add("left",1);else if(kind==0x204)store.Add("right",1);else if(kind==0x207)store.Add("middle",1);
            else if(kind==0x20B)store.Add("side",1);
            else if(kind==0x20A || kind==0x20E)store.Add("wheel",Math.Abs((short)(m.Data>>16)/120.0));
            else if(kind==0x200) {var next=new Point(m.X,m.Y);if(previous.HasValue){double x=next.X-previous.Value.X,y=next.Y-previous.Value.Y;store.Add("pixels",Math.Sqrt(x*x+y*y));}previous=next;}
        }
        return CallNextHookEx(mouseHook,code,message,data);
    }
    IntPtr KeyboardEvent(int code,IntPtr message,IntPtr data) {
        if(code>=0) {
            int key=Marshal.ReadInt32(data),kind=message.ToInt32();
            if(key>=0 && key<down.Length) {
                if(kind==0x100 || kind==0x104){if(!down[key])store.Add("keys",1);down[key]=true;}
                else if(kind==0x101 || kind==0x105)down[key]=false;
            }
        }
        return CallNextHookEx(keyboardHook,code,message,data);
    }
    void SampleNetwork() {
        try {
            uint index;if(GetBestInterface(0x08080808,out index)!=0)throw new Exception("No default IPv4 route");
            var matches=NetworkInterface.GetAllNetworkInterfaces().Where(n=>n.OperationalStatus==OperationalStatus.Up && n.Supports(NetworkInterfaceComponent.IPv4) && n.GetIPProperties().GetIPv4Properties()!=null && n.GetIPProperties().GetIPv4Properties().Index==index).ToArray();
            if(matches.Length!=1)throw new Exception("No unique routed network interface");
            var nic=matches[0];var stats=nic.GetIPv4Statistics();
            if(networkId==nic.Id && stats.BytesReceived>=previousRx && stats.BytesSent>=previousTx) {
                store.Add("received",stats.BytesReceived-previousRx);store.Add("sent",stats.BytesSent-previousTx);
            }
            networkId=nic.Id;previousRx=stats.BytesReceived;previousTx=stats.BytesSent;
            network="Network: "+nic.Name+" (selected IPv4 route; interface bytes)";
        } catch(Exception e){networkId="";network="Network unavailable: "+e.Message;}
    }
    double Clicks(bool today){return new[]{"left","right","middle","side"}.Sum(k=>store.Total(k,today));}
    static string Bytes(double bytes){string[] units={"B","KiB","MiB","GiB","TiB"};int i=0;while(bytes>=1024 && i<4){bytes/=1024;i++;}return bytes.ToString(i==0 || bytes>=100?"N0":bytes>=10?"N1":"N2")+" "+units[i];}
    void UpdateDisplay() {
        compact.Hint="All-time clicks: "+Clicks(false).ToString("N0")+"\nAll-time keystrokes: "+store.Total("keys",false).ToString("N0")+"\nClick for totals and history.";
        string tip=Clicks(false).ToString("N0")+" clicks / "+store.Total("keys",false).ToString("N0")+" keys";tray.Text=tip.Substring(0,Math.Min(63,tip.Length));
        details.Footer=saveError!=""?saveError:network;
        if(details.Visible)details.Invalidate();
    }
    float PaintActivity(Graphics g){
        Color blue=Color.FromArgb(61,174,233),green=Color.FromArgb(39,174,96);
        string[] labels={"clicks","keystrokes","download","upload"};
        string[] values={Clicks(false).ToString("N0"),store.Total("keys",false).ToString("N0"),Bytes(store.Total("received",false)),Bytes(store.Total("sent",false))};
        for(int i=0;i<4;i++){
            float x=24+i*104;
            using(var pen=new Pen(details.Muted,1.2f)){
                if(i==0){using(var shape=TaskbarSurface.Rounded(new RectangleF(x,9,10,16),4))g.DrawPath(pen,shape);g.DrawLine(pen,x+5,11,x+5,16);}
                else if(i==1){using(var shape=TaskbarSurface.Rounded(new RectangleF(x,12,14,10),2))g.DrawPath(pen,shape);for(int c=0;c<3;c++)g.DrawLine(pen,x+3+c*4,15,x+3+c*4,17);g.DrawLine(pen,x+4,20,x+10,20);}
                else {float top=i==2?11:23,bottom=i==2?23:11;g.DrawLine(pen,x+6,top,x+6,bottom);g.DrawLine(pen,x+2,bottom+(i==2?-4:4),x+6,bottom);g.DrawLine(pen,x+10,bottom+(i==2?-4:4),x+6,bottom);}
            }
            details.Label(g,values[i],x+20,0,82,15,details.Ink,true);
            details.Label(g,labels[i],x+20,24,82,10,details.Muted);
            details.Hint(new RectangleF(x,0,103,45),values[i]+" "+labels[i]);
        }
        float y=55;
        using(var pen=new Pen(details.Line))g.DrawLine(pen,24,y,436,y);y+=16;
        double max=new[]{"left","right","middle","side"}.Max(k=>store.Total(k,false));
        string[] keys={"left","right","middle","side"},titles={"Left","Right","Middle","Extra"};
        for(int i=0;i<keys.Length;i++){
            double value=store.Total(keys[i],false);if(i>=2 && value==0)continue;
            details.Label(g,titles[i],24,y,70,12,details.Muted);
            details.Label(g,value.ToString("N0"),96,y,80,12,details.Ink,false,true);
            details.Bar(g,190,y+8,246,max>0?value/max*100:0,blue);y+=29;
        }
        using(var pen=new Pen(details.Line))g.DrawLine(pen,24,y,436,y);y+=13;
        details.Label(g,"Scroll",24,y,180,12,details.Muted);
        details.Label(g,store.Total("wheel",false).ToString("N0")+" notches",210,y,226,12,details.Ink,false,true);
        details.Hint(new RectangleF(24,y,412,22),"Wheel and touchpad scrolling combined in Windows wheel units.");y+=26;
        details.Label(g,"Pointer travel",24,y,180,12,details.Muted);
        double metres=store.Total("pixels",false)/96*0.0254;
        details.Label(g,(metres>=1000?(metres/1000).ToString("N3")+" km":metres.ToString("N2")+" m")+" (est.)",210,y,226,12,details.Ink,false,true);
        details.Hint(new RectangleF(24,y,412,22),"Screen-pointer distance estimated at 96 pixels/inch; not physical mouse travel. "+store.Total("pixels",false).ToString("N0")+" screen pixels.");y+=39;
        details.Label(g,"Input activity (24h)",24,y,412,12,details.Ink,true);y+=27;
        PaintHistory(g,y,false,blue,green);y+=88;
        details.Label(g,"Network activity (24h)",24,y,200,12,details.Ink,true);
        details.Label(g,"■ Download",250,y,100,10,blue);details.Label(g,"■ Upload",356,y,80,10,green);y+=27;
        PaintHistory(g,y,true,blue,green);y+=88;
        if(saveError!="")y+=details.Wrapped(g,saveError,24,y,412,11,Color.IndianRed);
        if(network.StartsWith("Network unavailable"))y+=details.Wrapped(g,network,24,y,412,11,Color.IndianRed);
        return y+6;
    }
    void PaintHistory(Graphics g,float y,bool networkGraph,Color blue,Color green){
        var first=new double[24];var second=new double[24];double max=1;var now=DateTime.UtcNow;
        for(int i=0;i<24;i++){
            Dictionary<string,double> v;
            if(!store.Hours.TryGetValue(now.AddHours(i-23).ToString("yyyy-MM-ddTHH:00:00Z",CultureInfo.InvariantCulture),out v))v=new Dictionary<string,double>();
            first[i]=networkGraph?CountStore.Value(v,"received"):new[]{"left","right","middle","keys"}.Sum(k=>CountStore.Value(v,k));
            second[i]=networkGraph?CountStore.Value(v,"sent"):0;max=Math.Max(max,Math.Max(first[i],second[i]));
        }
        for(int i=0;i<24;i++){
            float x=24+i*(412f/24),width=412f/24-1;
            float h1=Math.Max(1,(float)(first[i]/max*60)),h2=Math.Max(1,(float)(second[i]/max*60));
            using(var brush=new SolidBrush(first[i]>0?blue:details.Line))g.FillRectangle(brush,x,y+60-h1,networkGraph?(width-1)/2:width,h1);
            if(networkGraph)using(var brush=new SolidBrush(second[i]>0?green:details.Line))g.FillRectangle(brush,x+(width+1)/2,y+60-h2,(width-1)/2,h2);
            string hour=now.AddHours(i-23).ToLocalTime().ToString("HH");
            if(i%6==0)details.Label(g,hour,x,y+63,32,9,details.Muted);
            details.Hint(new RectangleF(x,y,width,78),hour+":00 — "+(networkGraph?"Download "+Bytes(first[i])+" · Upload "+Bytes(second[i]):first[i].ToString("N0")+" actions"));
        }
    }
    void ShowCompact(){counterEnabled=true;DockCounter();}
    void DockCounter(){compact.EnabledOnTaskbar=counterEnabled;compact.RefreshSurface();}
    void PaintCounter(Graphics g,bool light){
        Color foreground=TaskbarSurface.Foreground(light),muted=TaskbarSurface.Secondary(light);
        using(var pen=new Pen(muted,1.25f)) {
            pen.StartCap=System.Drawing.Drawing2D.LineCap.Round;pen.EndCap=System.Drawing.Drawing2D.LineCap.Round;
            using(var path=TaskbarSurface.Rounded(new RectangleF(10,7,10,15),4))g.DrawPath(pen,path);
            g.DrawLine(pen,15,9,15,13);
            using(var path=TaskbarSurface.Rounded(new RectangleF(7,29,17,11),2))g.DrawPath(pen,path);
            for(int row=0;row<2;row++)for(int col=0;col<4;col++)g.DrawLine(pen,10+col*3,32+row*3,10.7f+col*3,32+row*3);
            g.DrawLine(pen,12,38,19,38);
        }
        TaskbarSurface.TextAt(g,Clicks(false).ToString("N0"),33,6,13,foreground,true);
        TaskbarSurface.TextAt(g,store.Total("keys",false).ToString("N0"),33,26,13,foreground,true);
        using(var pen=new Pen(Color.FromArgb(45,muted),1))g.DrawLine(pen,102,12,102,36);
    }
    void ShowDetails(){details.ToggleAt(compact);UpdateDisplay();}
    void Save(){try{store.Save(path);saveError="";}catch(Exception e){saveError="Counts could not be saved: "+e.Message;details.Footer=saveError;details.Invalidate();}}
    protected override void ExitThreadCore(){timer.Stop();UnhookWindowsHookEx(mouseHook);UnhookWindowsHookEx(keyboardHook);Save();Microsoft.Win32.SystemEvents.SessionEnding-=SessionEnding;tray.Visible=false;tray.Dispose();compact.Dispose();details.Dispose();timer.Dispose();base.ExitThreadCore();}
}
