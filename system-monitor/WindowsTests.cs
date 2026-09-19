using System;
using System.Linq;
using System.Threading;

static class WindowsTests {
    static int checks;
    static void Check(bool value,string message){checks++;if(!value)throw new Exception(message);}
    [STAThread] static int Main(){
        try{
            Check(MetricsReader.Percent(10,0)==0,"Disabled page file must not divide by zero");
            Check(MetricsReader.Percent(1,4)==25,"Incorrect utilization ratio");
            Check(MetricsReader.Percent(-1,10)==0 && MetricsReader.Percent(20,10)==100,"Utilization out of bounds");
            using(var reader=new MetricsReader()){
                reader.Sample(true);Thread.Sleep(1200);var data=reader.Sample(true);
                Check(!double.IsNaN(data.Cpu) && data.Cpu>=0 && data.Cpu<=100,"No valid CPU readings");
                Check(data.MemoryTotal>0 && data.MemoryAvailable<=data.MemoryTotal,"Invalid physical memory totals");
                Check(Math.Abs(data.Ram-MetricsReader.Percent(data.MemoryTotal-data.MemoryAvailable,data.MemoryTotal))<0.01,"RAM percentage differs from used memory");
                Check(data.Cores.Length==Environment.ProcessorCount && data.Cores.All(v=>!double.IsNaN(v) && v>=0 && v<=100),"Invalid per-core readings");
                Check(!double.IsNaN(data.Disk) && data.Disk>=0 && data.Disk<=100,"Invalid disk activity");
                Check(data.Read>=0 && data.Write>=0,"Disk throughput unavailable");
                Check(data.PageError=="" && data.Pages.All(p=>p.Used<=p.Total && p.Used>=0),"Page-file readings unavailable or invalid");
                Check(data.Swap==MetricsReader.Percent(data.Pages.Sum(p=>p.Used),data.Pages.Sum(p=>p.Total)),"Swap is not actual page-file use");
                Check(data.Volumes.Length>0 && data.Volumes.All(v=>v.Free>=0 && v.Free<=v.Total),"Invalid storage capacity");
                Check(data.Processes.Length>0 && data.Processes.Any(p=>!double.IsNaN(p.Cpu)),"Process CPU deltas unavailable");
                Check(data.Processes.All(p=>p.Memory>=0 && (double.IsNaN(p.Cpu) || p.Cpu>=0 && p.Cpu<=100)),"Invalid process metrics");
                Console.WriteLine("CPU {0:N1}% | RAM {1:N1}% ({2:N2} GiB) | Disk {3:N1}% | Swap {4:N1}% ({5:N0} MiB)",data.Cpu,data.Ram,data.MemoryTotal/1073741824,data.Disk,data.Swap,data.Pages.Sum(p=>p.Used)/1048576);
            }
            Console.WriteLine("Passed "+checks+" live metric checks.");return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
