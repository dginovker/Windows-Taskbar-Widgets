using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

static class Program {
    [STAThread] static void Main(string[] args) {
        bool created;
        using (var mutex = new Mutex(true, "Local\\AIUsageRings.Windows", out created)) {
            if (!created) return;

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try {using (var app = new UsageApp()) Application.Run(app);}
            catch(Exception error){MessageBox.Show(error.Message,"AI Usage could not start");}
        }
    }
}

sealed class UsageApp : ApplicationContext {
    readonly string[] names = { "claude", "codex" };
    readonly NotifyIcon[] icons = new NotifyIcon[2];
    readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
    readonly WidgetFlyout popup = new WidgetFlyout("AI Usage Rings",600,580);
    readonly TaskbarSurface taskbar = new TaskbarSurface("AI Usage","AIUsageRings",124,268);
    readonly string displayPreference = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIUsageRings", "hide-taskbar-display");
    string selectedPeriod="30d";
    readonly bool[] showModels=new bool[2];
    readonly string python, helper;
    Dictionary<string, object> data = new Dictionary<string, object>();
    bool busy, exiting, failed;
    string message = "Waiting for first update";
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr icon);

    public UsageApp() {
        string root = AppDomain.CurrentDomain.BaseDirectory;
        python = File.ReadAllText(Path.Combine(root, "python-path.txt")).Trim();
        helper = Path.Combine(root,"widget_snapshot.py");
        popup.Heading="AI Usage";
        popup.Subheading="Subscription limits and local token activity";
        popup.PaintBody=PaintUsagePopup;
        popup.HeaderAction=async delegate {await RefreshUsage();};
        popup.HeaderHint=delegate {return popup.Footer+". Click to refresh.";};
        taskbar.PaintContent=PaintTaskbarUsage;
        taskbar.Shutdown=delegate {ExitThread();};
        taskbar.MouseClick+=delegate(object sender,MouseEventArgs e){if(e.Button==MouseButtons.Left)ShowPopup();};
        for (int i = 0; i < names.Length; i++) {
            var menu = new ContextMenuStrip();
            menu.Items.Add("Show usage", null, delegate { ShowPopup(); });
            var displayToggle = new ToolStripMenuItem("Show taskbar usage") { Checked = !File.Exists(displayPreference) };
            displayToggle.Click += delegate { SetTaskbar(!taskbar.Visible); };
            menu.Opening += delegate { displayToggle.Checked = taskbar.Visible; };
            menu.Items.Add(displayToggle);
            menu.Items.Add("Refresh", null, async delegate { await RefreshUsage(); });
            menu.Items.Add("Exit", null, delegate { ExitThread(); });
            if(i==0){taskbar.ContextMenuStrip=menu;taskbar.AddPlacementMenu(menu);}
            icons[i] = new NotifyIcon { Visible = false, Text = Title(names[i]) + " usage: loading", ContextMenuStrip = menu };
            icons[i].MouseClick += delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) { if (popup.Visible) popup.Hide(); else ShowPopup(); } };
            SetIcon(i, new Dictionary<string, object>());
        }
        timer.Interval = 600000; timer.Tick += async delegate { await RefreshUsage(); }; timer.Start();
        taskbar.VisibleChanged+=delegate {if(!exiting)icons[0].Visible=!taskbar.Visible;};
        popup.Handle.ToInt64(); // Create the UI synchronization context before starting the worker.
        taskbar.EnabledOnTaskbar=!File.Exists(displayPreference);
        icons[0].Visible=!taskbar.EnabledOnTaskbar;
        Render();
        if (!File.Exists(displayPreference)) ShowTaskbar();
        popup.BeginInvoke(new Action(async delegate { await RefreshUsage(); }));
    }
    static string Title(string name) { return char.ToUpper(name[0]) + name.Substring(1); }
    static Dictionary<string, object> Parse(string json) { return new JavaScriptSerializer { MaxJsonLength = 16000000 }.Deserialize<Dictionary<string, object>>(json); }
    // A Python traceback ends with the exception line, which is what explains the failure.
    internal static string LastLine(string text) {
        var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = lines.Length - 1; i >= 0; i--) if (lines[i].Trim().Length > 0) return lines[i].Trim();
        return "no error output";
    }
    static Dictionary<string, object> Obj(Dictionary<string, object> source, string key) {
        object value; return source.TryGetValue(key, out value) && value is Dictionary<string, object> ? (Dictionary<string, object>)value : new Dictionary<string, object>();
    }
    static string Str(Dictionary<string, object> source, string key, string fallback = "") {
        object value; return source.TryGetValue(key, out value) && value != null ? Convert.ToString(value) : fallback;
    }
    static double Used(Dictionary<string, object> quota) {
        object value; return quota.TryGetValue("used", out value) && value != null ? Convert.ToDouble(value) : -1;
    }
    static Color Accent(Dictionary<string, object> quota) {
        try { return ColorTranslator.FromHtml(Str(quota, "color", "#718096")); } catch { return Color.Gray; }
    }
    static string Percent(Dictionary<string, object> quota) { double value = Used(quota); return value < 0 ? "--" : Math.Round(value) + "%"; }
    static void Ring(Graphics g, RectangleF bounds, float width, Dictionary<string, object> quota) {
        using (var background = new Pen(Color.FromArgb(65, 72, 86), width)) g.DrawEllipse(background, bounds);
        double value = Used(quota);
        if (value > 0) using (var pen = new Pen(Accent(quota), width)) { pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round; g.DrawArc(pen, bounds, -90, (float)(Math.Min(100, value) * 3.6)); }
    }
    static Bitmap DrawRings(Dictionary<string, object> provider, int size) {
        var bitmap = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bitmap)) {
            g.SmoothingMode = SmoothingMode.AntiAlias; float scale = size / 32f;
            Ring(g, new RectangleF(3*scale, 3*scale, 26*scale, 26*scale), 3*scale, Obj(provider, "weekly"));
            Ring(g, new RectangleF(8*scale, 8*scale, 16*scale, 16*scale), 3*scale, Obj(provider, "current"));
            string days = Str(Obj(provider, "weekly"), "days", "?");
            using (var font = new Font("Segoe UI", 8*scale, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString(days, font, Brushes.White, new RectangleF(0, 0, size, size), format);
        }
        return bitmap;
    }
    void SetIcon(int index, Dictionary<string, object> provider) {
        using (var bitmap = DrawRings(provider, 32)) {
            IntPtr handle = bitmap.GetHicon();
            try { using (var borrowed = Icon.FromHandle(handle)) { var old = icons[index].Icon; icons[index].Icon = (Icon)borrowed.Clone(); if (old != null) old.Dispose(); } }
            finally { DestroyIcon(handle); }
        }
        string text = Title(names[index]) + " | 5h " + Percent(Obj(provider,"current")) + " | week " + Percent(Obj(provider,"weekly"));
        if (failed || provider.ContainsKey("error") || provider.ContainsKey("stale")) text += " | stale/error";
        icons[index].Text = text.Substring(0, Math.Min(63, text.Length));
    }
    void ShowTaskbar() {taskbar.EnabledOnTaskbar=true;taskbar.RefreshSurface();}
    void SetTaskbar(bool visible) {
        icons[0].Visible=!visible;
        Directory.CreateDirectory(Path.GetDirectoryName(displayPreference));
        if (visible) { if (File.Exists(displayPreference)) File.Delete(displayPreference); ShowTaskbar(); }
        else { File.WriteAllText(displayPreference, "hidden"); taskbar.EnabledOnTaskbar=false;taskbar.Hide(); }
    }
    void PaintTaskbarUsage(Graphics g,bool light) {
        for(int i=0;i<names.Length;i++) {
            var p=Obj(data,names[i]);var weekly=Obj(p,"weekly");var current=Obj(p,"current");
            float x=i*136+3;
            Color accent=i==0?Color.FromArgb(192,105,74):Color.FromArgb(31,145,130);
            Color inner=Color.FromArgb(190,accent),track=Color.FromArgb(light?32:55,TaskbarSurface.Foreground(light));
            DrawArc(g,new RectangleF(x+2,8,32,32),3,Used(weekly),accent,track);
            DrawArc(g,new RectangleF(x+8,14,20,20),2.4f,Used(current),inner,track);
            string days=Str(weekly,"days","?");
            using(var font=new Font("Segoe UI",10,FontStyle.Bold,GraphicsUnit.Pixel))
            using(var brush=new SolidBrush(TaskbarSurface.Foreground(light)))
            using(var format=new StringFormat{Alignment=StringAlignment.Center,LineAlignment=StringAlignment.Center})
                g.DrawString(days,font,brush,new RectangleF(x+8,14,20,20),format);
            TaskbarSurface.TextAt(g,Title(names[i]),x+44,7,11,TaskbarSurface.Secondary(light));
            TaskbarSurface.TextAt(g,Percent(weekly),x+44,23,14,TaskbarSurface.Foreground(light),true);
            TaskbarSurface.TextAt(g,"week",x+81,27,9,TaskbarSurface.Secondary(light));
            if(failed||p.ContainsKey("error")||p.ContainsKey("stale"))using(var brush=new SolidBrush(Color.DarkOrange))g.FillEllipse(brush,x+32,6,5,5);
        }
        taskbar.Hint=(failed?message+"\n":"")+"Claude: 5h "+Percent(Obj(Obj(data,"claude"),"current"))+" / week "+Percent(Obj(Obj(data,"claude"),"weekly"))+
            "\nCodex: 5h "+Percent(Obj(Obj(data,"codex"),"current"))+" / week "+Percent(Obj(Obj(data,"codex"),"weekly"))+"\nOuter: week / inner: 5h / centre: reset days. Click for details.";
    }
    static void DrawArc(Graphics g,RectangleF bounds,float stroke,double used,Color color,Color track) {
        using(var pen=new Pen(track,stroke))g.DrawEllipse(pen,bounds);
        if(used<=0)return;
        using(var pen=new Pen(color,stroke)){pen.StartCap=LineCap.Round;pen.EndCap=LineCap.Round;g.DrawArc(pen,bounds,-90,(float)(Math.Min(100,used)*3.6));}
    }
    async void ShowPopup() {bool opening=!popup.Visible;popup.ToggleAt(taskbar);if(opening)await RefreshUsage();}
    async Task RefreshUsage() {
        if (busy || exiting) return;
        busy = true; popup.Footer = "Refreshing usage since "+DateTime.Now.ToString("HH:mm:ss")+"...";popup.Invalidate();
        try {
            string result = await Task.Run(() => {
                var start = new ProcessStartInfo(python, "\"" + helper + "\" --providers=claude,codex") {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    WorkingDirectory = Path.GetDirectoryName(helper)
                };
                start.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
                using (var process = Process.Start(start)) {
                    var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
                    if (!process.WaitForExit(90000)) { process.Kill(); throw new Exception("Usage lookup timed out; retry from the menu."); }
                    Task.WaitAll(output, error);
                    if (process.ExitCode != 0) throw new Exception("Collector exited with code " + process.ExitCode + ": " + LastLine(error.Result));
                    return output.Result;
                }
            });
            if (exiting) return;
            data = Parse(result); failed = false; message = "Updated " + DateTime.Now.ToString("HH:mm:ss");
            // Save usage only, never credentials, for diagnostics and offline inspection.
            string cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIUsageRings");
            Directory.CreateDirectory(cache); File.WriteAllText(Path.Combine(cache, "snapshot.json"), result);
        } catch (Exception error) { failed = true; message = "Update failed: " + error.Message + " Previous readings may be stale."; }
        finally { if (!exiting) { busy = false; Render(); } }
    }
    void Render() {
        for(int i=0;i<names.Length;i++)SetIcon(i,Obj(data,names[i]));
        popup.Footer=message;popup.Invalidate();taskbar.RefreshSurface();
    }
    System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string,Dictionary<string,object>>> PopupQuotas(Dictionary<string,object> provider){
        var result=new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string,Dictionary<string,object>>>();
        object rows;
        if(provider.TryGetValue("rows",out rows) && rows is IEnumerable)foreach(object item in (IEnumerable)rows){
            var row=item as Dictionary<string,object>;if(row!=null)result.Add(new System.Collections.Generic.KeyValuePair<string,Dictionary<string,object>>(Str(row,"title"),row.ContainsKey("data")?Obj(row,"data"):Obj(provider,Str(row,"key"))));
        }
        if(result.Count==0){if(Used(Obj(provider,"current"))>=0)result.Add(new KeyValuePair<string,Dictionary<string,object>>("5h",Obj(provider,"current")));result.Add(new KeyValuePair<string,Dictionary<string,object>>("Week",Obj(provider,"weekly")));}
        return result;
    }
    float PaintUsagePopup(Graphics g){
        float bottom=0;
        for(int i=0;i<names.Length;i++){
            float x=24+i*282,width=270;var p=Obj(data,names[i]);var weekly=Obj(p,"weekly");var current=Obj(p,"current");
            popup.Centered(g,Title(names[i]),x,0,width,13,popup.Ink,true);
            DrawArc(g,new RectangleF(x+105,30,60,60),5,Used(weekly),Accent(weekly),popup.Line);
            if(Used(current)>=0)DrawArc(g,new RectangleF(x+116,41,38,38),4,Used(current),Accent(current),popup.Line);
            popup.Centered(g,Str(weekly,"days","?"),x+116,48,38,16,popup.Ink,true);
            popup.Centered(g,Str(weekly,"reset_label","--"),x,103,width,11,popup.Muted);
            float y=132;
            foreach(var entry in PopupQuotas(p)){
                popup.Label(g,entry.Key,x,y,170,12,popup.Ink,true);
                popup.Label(g,Percent(entry.Value)+(Used(entry.Value)<0?"":" used"),x+170,y,100,12,popup.Ink,false,true);
                popup.Bar(g,x,y+23,width,Used(entry.Value),Accent(entry.Value));
                string pace=Str(entry.Value,"pace","--"),reset=Str(entry.Value,"reset_label","--");
                popup.Label(g,pace,x,y+34,145,10,popup.Muted);
                popup.Label(g,reset,x+145,y+34,125,10,popup.Muted,false,true);
                popup.Hint(new RectangleF(x,y+30,width,22),pace+" | "+reset);y+=58;
            }
            y+=popup.Wrapped(g,Str(p,"stale"),x,y,width,11,Color.DarkGoldenrod);
            var resets=Obj(p,"global_resets");
            if(Str(p,"account")!="" || resets.Count>0)y+=12;
            if(Str(p,"account")!="")y+=popup.Wrapped(g,"Account: "+Str(p,"account"),x,y,width,11,popup.Muted);
            foreach(string key in new[]{"past","next","banked"})if(Str(resets,key)!="")
                y+=popup.Wrapped(g,(key=="next"?"Next odds":Title(key))+": "+Str(resets,key),x,y,width,11,popup.Muted);
            bottom=Math.Max(bottom,y);
        }
        float section=bottom+16;
        popup.Label(g,"API cost",24,section+5,100,13,popup.Ink,true);
        string[] periods={"24h","7d","30d","lifetime"};
        for(int i=0;i<periods.Length;i++){string key=periods[i];popup.Button(g,key=="lifetime"?"All":key,new RectangleF(116+i*52,section,46,26),delegate {selectedPeriod=key;},selectedPeriod==key);}
        float end=section+40;
        for(int i=0;i<names.Length;i++){
            int index=i;float x=24+i*282,y=section+40;var cost=new Dictionary<string,object>();object windows;
            if(Obj(data,"tokens").TryGetValue("windows",out windows) && windows is IEnumerable)foreach(object w in (IEnumerable)windows){var win=w as Dictionary<string,object>;if(win!=null&&Str(win,"key")==selectedPeriod)cost=Obj(Obj(win,"providers"),names[i]);}
            if(Str(cost,"tokens")=="" && Str(cost,"cost")=="")continue;
            popup.Label(g,Str(cost,"tokens","--"),x,y,270,17,popup.Ink);y+=29;
            popup.Label(g,Str(cost,"cost","--")+" API equivalent",x,y,228,12,popup.Muted);
            object models;
            if(cost.TryGetValue("models",out models) && models is IEnumerable){
                popup.Button(g,showModels[i]?"−":"+",new RectangleF(x+238,y-4,32,25),delegate {showModels[index]=!showModels[index];});y+=30;
                if(showModels[i])foreach(object m in (IEnumerable)models){var model=m as Dictionary<string,object>;if(model==null)continue;
                    popup.Label(g,Str(model,"name"),x,y,207,11,popup.Muted);popup.Label(g,Str(model,"cost"),x+210,y,60,11,popup.Ink,false,true);
                    popup.Hint(new RectangleF(x,y,270,22),Str(model,"name")+": "+Str(model,"cost"));y+=23;
                }
            }else y+=28;
            if(showModels[i])y+=popup.Wrapped(g,Str(cost,"note"),x,y,270,11,popup.Muted);
            end=Math.Max(end,y);
        }
        object available;
        if(Obj(data,"claude").TryGetValue("available",out available) && available is bool && !(bool)available)
            end+=popup.Wrapped(g,"Claude usage will appear after the next Claude Code response.",24,end,552,11,popup.Muted);
        end+=popup.Wrapped(g,Str(Obj(data,"tokens"),"note"),24,end,552,11,popup.Muted);
        if(failed)end+=popup.Wrapped(g,message,24,end,552,11,Color.DarkGoldenrod);
        object errors;if(data.TryGetValue("errors",out errors) && errors is IEnumerable)foreach(object error in (IEnumerable)errors)
            end+=popup.Wrapped(g,Convert.ToString(error),24,end,552,11,Color.DarkGoldenrod);
        return end+12;
    }
    protected override void ExitThreadCore() {

        exiting = true; timer.Stop(); timer.Dispose();

        foreach (var icon in icons) { icon.Visible = false; var image = icon.Icon; icon.Dispose(); if (image != null) image.Dispose(); }

        taskbar.Dispose();

        popup.Dispose(); base.ExitThreadCore();

    }

}
