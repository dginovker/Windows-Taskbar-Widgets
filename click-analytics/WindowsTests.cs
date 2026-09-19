using System;
using System.Drawing;
using System.IO;
using System.Globalization;

static class WindowsTests {
    static int checks;
    static void Check(bool value,string message){checks++;if(!value)throw new Exception(message);}
    [STAThread] static int Main(){
        try {
            Layout();
            SurfaceStyles();
            Persistence();
            Console.WriteLine("Passed "+checks+" Windows regression checks.");return 0;
        } catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern int GetWindowLong(IntPtr window,int index);
    static void SurfaceStyles(){
        using(var surface=new TaskbarSurface("Widget style test","WidgetStyleTest",0,100)){
            int styles=GetWindowLong(surface.Handle,-20);
            Check((styles & 0x8)!=0,"Taskbar surface must stay above the taskbar after handle creation");
            Check((styles & 0x08080000)==0x08080000,"Surface must be layered and must not activate on click");
        }
    }
    static void Layout(){
        Rectangle result;
        foreach(float scale in new[]{1f,1.25f,1.5f,2f}){
            var screen=new Rectangle(-2560,-200,2560,1600);
            int height=(int)(48*scale);
            var bar=new Rectangle(screen.Left,screen.Bottom-height,screen.Width,height);
            Check(TaskbarLayout.TryStrip(bar,screen,scale,268,124,out result),"Visible secondary taskbar rejected");
            Check(result.Left==screen.Left+(int)(124*scale),"Offset must scale once on negative-coordinate monitor");
            Check(result.Width==(int)Math.Ceiling(268*scale),"Incorrect physical pixel width");
            Check(bar.Contains(result),"Strip extends outside taskbar");
            Check(TaskbarLayout.TryStrip(bar,screen,scale,268,20000,out result) && bar.Contains(result),"Offset must clamp on smaller monitors");
            var hidden=new Rectangle(bar.Left,screen.Bottom-2,bar.Width,bar.Height);
            Check(!TaskbarLayout.TryStrip(hidden,screen,scale,268,12,out result),"Auto-hidden bottom taskbar remains over windows");
            var top=new Rectangle(bar.Left,screen.Top,bar.Width,bar.Height);
            Check(TaskbarLayout.TryStrip(top,screen,scale,268,12,out result),"Top taskbar rejected");
            top.Y=screen.Top-top.Height+2;
            Check(!TaskbarLayout.TryStrip(top,screen,scale,268,12,out result),"Auto-hidden top taskbar remains over windows");
        }
        Check(!TaskbarLayout.TryStrip(new Rectangle(0,0,48,1080),new Rectangle(0,0,1920,1080),1,268,0,out result),"Unsupported vertical taskbar must not cover the desktop");
        foreach(var work in new[]{new Rectangle(0,0,1920,1032),new Rectangle(-1280,40,1280,984),new Rectangle(0,0,480,640)}){
            foreach(var anchor in new[]{new Rectangle(work.Left,work.Bottom,300,48),new Rectangle(work.Right-20,work.Top-48,300,48)}){
                var popup=TaskbarLayout.Flyout(anchor,work,new Size(900,1020),12);
                Check(work.Contains(popup),"Flyout is off-screen");
                Check(popup.Width<=work.Width-16 && popup.Height<=work.Height-16,"Flyout cannot fit compact screen");
            }
        }
    }
    static void Persistence(){
        string folder=Path.Combine(Path.GetTempPath(),"click-widget-tests-"+Guid.NewGuid());
        string path=Path.Combine(folder,"counts.json");
        try{
            var store=new CountStore();store.Add("left",2);store.Add("left",1);store.Add("keys",4);store.Add("wheel",0.5);
            store.Save(path);
            var loaded=CountStore.Load(path);
            Check(loaded.Total("left",false)==3 && loaded.Total("keys",true)==4,"Restart loses totals");
            Check(loaded.Total("wheel",false)==0.5,"Fractional scrolling is truncated");
            loaded.Add("right",1);loaded.Save(path);
            Check(CountStore.Load(path).Total("right",false)==1,"Update loses new count");
            Check(CountStore.Load(path+".bak").Total("right",false)==0,"Backup does not retain previous checkpoint");
            var culture=CultureInfo.CurrentCulture;
            try{CultureInfo.CurrentCulture=new CultureInfo("ja-JP");Check(loaded.Total("left",true)==3,"Daily counts depend on locale");}
            finally{CultureInfo.CurrentCulture=culture;}
            File.WriteAllText(path,"{\"Hours\":null}");
            bool rejected=false;
            try{CountStore.Load(path);}catch(InvalidDataException){rejected=true;}
            Check(rejected,"Corrupt history must not be silently reset");
        }finally{
            foreach(string file in new[]{path,path+".bak",path+".tmp"})if(File.Exists(file))File.Delete(file);
            if(Directory.Exists(folder))Directory.Delete(folder);
        }
    }
}
