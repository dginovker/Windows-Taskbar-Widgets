using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

static class Program {
    [STAThread] static void Main(){
        bool created;using(var mutex=new Mutex(true,@"Local\SystemMonitor.Windows",out created)){
            if(!created)return;Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            try{using(var app=new MonitorApp())Application.Run(app);}catch(Exception error){MessageBox.Show(error.Message,"System Monitor could not start");}
        }
    }
}

sealed class MonitorApp : ApplicationContext {
    readonly TaskbarSurface surface=new TaskbarSurface("System monitor bars","SystemMonitor",400,44);
    readonly WidgetFlyout popup=new WidgetFlyout("System Monitor",320,340);
    readonly System.Windows.Forms.Timer timer=new System.Windows.Forms.Timer {Interval=1000};
    readonly MetricsReader reader=new MetricsReader();
    readonly NotifyIcon tray=new NotifyIcon {Icon=SystemIcons.Application,Text="System Monitor"};
    readonly string preference=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"SystemMonitor","disk-space");
    readonly string[] names={"CPU","RAM","Disk","Swap"};
    readonly Color[] colors={Color.FromArgb(61,174,233),Color.FromArgb(155,113,207),Color.FromArgb(39,174,130),Color.FromArgb(225,158,61)};
    Snapshot data=new Snapshot();bool busy,exiting,spaceMode;
    public MonitorApp(){
        spaceMode=File.Exists(preference);popup.Heading="System Monitor";popup.PaintBody=PaintDetails;
        surface.PaintContent=PaintBars;surface.Shutdown=delegate {ExitThread();};
        surface.MouseClick+=delegate(object sender,MouseEventArgs e){if(e.Button==MouseButtons.Left)Open();};
        var menu=new ContextMenuStrip();menu.Items.Add("Show system details",null,delegate {Open();});
        var diskMode=new ToolStripMenuItem("Disk bar shows storage used") {Checked=spaceMode,CheckOnClick=true};
        diskMode.CheckedChanged+=delegate {spaceMode=diskMode.Checked;Directory.CreateDirectory(Path.GetDirectoryName(preference));if(spaceMode)File.WriteAllText(preference,"space");else if(File.Exists(preference))File.Delete(preference);surface.RefreshSurface();};menu.Items.Add(diskMode);
        surface.AddPlacementMenu(menu);menu.Items.Add("Task Manager",null,delegate {TaskManager();});menu.Items.Add("Exit",null,delegate {ExitThread();});
        surface.ContextMenuStrip=menu;tray.ContextMenuStrip=menu;tray.MouseClick+=delegate(object sender,MouseEventArgs e){if(e.Button==MouseButtons.Left)Open();};
        surface.VisibleChanged+=delegate {if(!exiting)tray.Visible=!surface.Visible;};
        popup.Handle.ToInt64();surface.RefreshSurface();tray.Visible=!surface.Visible;timer.Tick+=async delegate {await Refresh();};timer.Start();
        popup.BeginInvoke(new Action(async delegate {await Refresh();}));
    }
    void Open(){popup.ToggleAt(surface);}
    static string Percent(double value){return double.IsNaN(value)?"--":Math.Round(value).ToString("0")+"%";}
    static string Bytes(double value){if(double.IsNaN(value))return "--";string[] units={"B","KiB","MiB","GiB","TiB"};int i=0;while(value>=1024 && i<4){value/=1024;i++;}return value.ToString(i==0?"N0":value>=100?"N0":value>=10?"N1":"N2")+" "+units[i];}
    async Task Refresh(){
        if(busy || exiting)return;busy=true;
        try{
            var next=await Task.Run(()=>reader.Sample(false));if(exiting)return;data=next;
            popup.Footer="Updated "+next.Time.ToString("HH:mm:ss");
            surface.RefreshSurface();if(popup.Visible)popup.Invalidate();
        }catch(Exception error){if(!exiting){popup.Footer="Update failed: "+error.Message;popup.Invalidate();}}
        finally{busy=false;if(exiting)reader.Dispose();}
    }
    double StoragePercent(){return data.Volumes.Length==0?double.NaN:MetricsReader.Percent(data.Volumes.Sum(v=>v.Total-v.Free),data.Volumes.Sum(v=>v.Total));}
    double Value(int index){return index==2 && spaceMode?StoragePercent():data.Value(index);}
    void PaintBars(Graphics g,bool light){
        for(int i=0;i<4;i++){
            float x=3+i*10;double value=Value(i);
            using(var shape=TaskbarSurface.Rounded(new RectangleF(x,8,7,32),2)){
                using(var brush=new SolidBrush(Color.FromArgb(light?45:65,colors[i])))g.FillPath(brush,shape);
                if(!double.IsNaN(value) && value>0){
                    float height=(float)(Math.Min(100,value)*32/100);
                    var state=g.Save();g.SetClip(shape);using(var brush=new SolidBrush(colors[i]))g.FillRectangle(brush,x,40-height,7,height);g.Restore(state);
                }
            }
        }
        surface.Hint="CPU "+Percent(data.Cpu)+"  |  RAM "+Percent(data.Ram)+"\nDisk "+(spaceMode?"storage "+Percent(StoragePercent()):"activity "+Percent(data.Disk))+"  |  Swap "+Percent(data.Swap)+"\nClick for details.";
    }
    float PaintDetails(Graphics g){
        string[] details={
            Environment.ProcessorCount+" logical processors",
            double.IsNaN(data.Ram)?"Unavailable":Bytes(data.MemoryTotal-data.MemoryAvailable)+" / "+Bytes(data.MemoryTotal)+" used",
            spaceMode?(data.Volumes.Length==0?"Unavailable":Bytes(data.Volumes.Sum(v=>v.Total-v.Free))+" / "+Bytes(data.Volumes.Sum(v=>v.Total))+" used"):
                "Read "+Bytes(data.Read)+"/s | Write "+Bytes(data.Write)+"/s",
            data.PageError.Length>0?"Unavailable":data.Pages.Length==0?"No page file configured":Bytes(data.Pages.Sum(p=>p.Used))+" / "+Bytes(data.Pages.Sum(p=>p.Total))+" used"
        };
        float y=0;
        for(int i=0;i<4;i++){
            using(var brush=new SolidBrush(colors[i]))g.FillEllipse(brush,24,y+6,7,7);
            popup.Label(g,names[i],39,y,150,12,popup.Ink,true);
            popup.Label(g,Percent(Value(i)),224,y,72,12,popup.Ink,true,true);
            popup.Label(g,details[i],39,y+23,257,11,popup.Muted);
            popup.Hint(new RectangleF(24,y,272,46),details[i]);y+=55;
        }
        if(data.Error.Length>0)y+=popup.Wrapped(g,data.Error,24,y,272,10,Color.DarkGoldenrod);
        if(data.PageError.Length>0)y+=popup.Wrapped(g,data.PageError,24,y,272,10,Color.DarkGoldenrod);
        popup.Button(g,"Open Task Manager",new RectangleF(24,y+3,272,28),TaskManager);return y+37;
    }
    void TaskManager(){try{Process.Start(new ProcessStartInfo("taskmgr.exe") {UseShellExecute=true});}catch(Exception error){popup.Footer=error.Message;popup.Invalidate();}}
    protected override void ExitThreadCore(){exiting=true;timer.Stop();timer.Dispose();tray.Visible=false;tray.Dispose();surface.Dispose();popup.Dispose();if(!busy)reader.Dispose();base.ExitThreadCore();}
}
