using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;

sealed class VolumeInfo { public string Name; public double Total,Free; }
sealed class PageInfo { public string Name; public double Total,Used,Peak; }
sealed class ProcessInfo { public string Name; public int Id; public double Cpu,Memory; }
sealed class Snapshot {
    public DateTime Time=DateTime.Now,SlowTime;
    public double Cpu=double.NaN,Ram=double.NaN,Disk=double.NaN,Swap=double.NaN;
    public double MemoryTotal,MemoryAvailable,Commit,CommitLimit,Cache;
    public double Read=double.NaN,Write=double.NaN,Queue=double.NaN,Latency=double.NaN;
    public string CpuName="",Error="",PageError="",DriveError="";
    public double[] Cores=new double[0];
    public VolumeInfo[] Volumes=new VolumeInfo[0];
    public PageInfo[] Pages=new PageInfo[0];
    public ProcessInfo[] Processes=new ProcessInfo[0];
    public double Value(int index){return index==0?Cpu:index==1?Ram:index==2?Disk:Swap;}
}

sealed class PdhReader : IDisposable {
    IntPtr query;
    readonly Dictionary<string,IntPtr> counters=new Dictionary<string,IntPtr>();
    [StructLayout(LayoutKind.Explicit)] struct Value { [FieldOffset(0)] public uint Status; [FieldOffset(8)] public double Number; }
    [DllImport("pdh.dll",CharSet=CharSet.Unicode)] static extern uint PdhOpenQuery(string source,UIntPtr user,out IntPtr query);
    [DllImport("pdh.dll",CharSet=CharSet.Unicode)] static extern uint PdhAddEnglishCounter(IntPtr query,string path,UIntPtr user,out IntPtr counter);
    [DllImport("pdh.dll")] static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll")] static extern uint PdhGetFormattedCounterValue(IntPtr counter,uint format,IntPtr type,out Value value);
    [DllImport("pdh.dll")] static extern uint PdhCloseQuery(IntPtr query);
    public PdhReader(){
        if(PdhOpenQuery(null,UIntPtr.Zero,out query)!=0)throw new InvalidOperationException("Windows performance counters are unavailable.");
        Add("cpu",@"\Processor Information(_Total)\% Processor Time");
        for(int i=0;i<Environment.ProcessorCount;i++)Add("core"+i,@"\Processor("+i+@")\% Processor Time");
        Add("idle",@"\PhysicalDisk(_Total)\% Idle Time");
        Add("read",@"\PhysicalDisk(_Total)\Disk Read Bytes/sec");
        Add("write",@"\PhysicalDisk(_Total)\Disk Write Bytes/sec");
        Add("queue",@"\PhysicalDisk(_Total)\Current Disk Queue Length");
        Add("latency",@"\PhysicalDisk(_Total)\Avg. Disk sec/Transfer");
        Collect();
    }
    void Add(string name,string path){IntPtr counter;if(PdhAddEnglishCounter(query,path,UIntPtr.Zero,out counter)==0)counters[name]=counter;}
    public void Collect(){PdhCollectQueryData(query);}
    public double Read(string name){IntPtr handle;Value value;if(!counters.TryGetValue(name,out handle) || PdhGetFormattedCounterValue(handle,0x200,IntPtr.Zero,out value)!=0 || value.Status>1)return double.NaN;return value.Number;}
    public void Dispose(){if(query!=IntPtr.Zero){PdhCloseQuery(query);query=IntPtr.Zero;}}
}

sealed class MetricsReader : IDisposable {
    PdhReader counters;
    DateTime slowUpdated=DateTime.MinValue,processUpdated=DateTime.MinValue;
    VolumeInfo[] volumes=new VolumeInfo[0];PageInfo[] pages=new PageInfo[0];
    string cpuName="",pageError="Waiting for page-file data",driveError="";
    readonly Dictionary<int,Tuple<long,double>> processTimes=new Dictionary<int,Tuple<long,double>>();
    [StructLayout(LayoutKind.Sequential)] sealed class MemoryStatus {
        public uint Length=(uint)Marshal.SizeOf(typeof(MemoryStatus)),Load;
        public ulong TotalPhysical,AvailablePhysical,TotalPageFile,AvailablePageFile,TotalVirtual,AvailableVirtual,AvailableExtended;
    }
    [StructLayout(LayoutKind.Sequential)] struct PerformanceInfo {
        public uint Size;
        public UIntPtr CommitTotal,CommitLimit,CommitPeak,PhysicalTotal,PhysicalAvailable,SystemCache,KernelTotal,KernelPaged,KernelNonpaged,PageSize;
        public uint Handles,Processes,Threads;
    }
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool GlobalMemoryStatusEx([In,Out] MemoryStatus value);
    [DllImport("psapi.dll",SetLastError=true)] static extern bool GetPerformanceInfo(out PerformanceInfo value,uint size);
    public static double Percent(double used,double total){return total<=0?0:Math.Max(0,Math.Min(100,used/total*100));}
    public Snapshot Sample(bool processes){
        var s=new Snapshot();
        try{
            if(counters==null)counters=new PdhReader();
            counters.Collect();s.Cpu=counters.Read("cpu");s.Disk=100-counters.Read("idle");
            if(!double.IsNaN(s.Disk))s.Disk=Math.Max(0,Math.Min(100,s.Disk));
            s.Read=counters.Read("read");s.Write=counters.Read("write");s.Queue=counters.Read("queue");s.Latency=counters.Read("latency")*1000;
            s.Cores=Enumerable.Range(0,Environment.ProcessorCount).Select(i=>counters.Read("core"+i)).ToArray();
            if(double.IsNaN(s.Cpu) || double.IsNaN(s.Disk))s.Error="Some performance counters are warming up or unavailable.";
        }catch(Exception error){s.Error=error.Message;}
        var memory=new MemoryStatus();
        if(GlobalMemoryStatusEx(memory)){
            s.MemoryTotal=memory.TotalPhysical;s.MemoryAvailable=memory.AvailablePhysical;
            s.Ram=Percent(s.MemoryTotal-s.MemoryAvailable,s.MemoryTotal);
            PerformanceInfo performance;
            if(GetPerformanceInfo(out performance,(uint)Marshal.SizeOf(typeof(PerformanceInfo)))){
                double size=performance.PageSize.ToUInt64();s.Commit=performance.CommitTotal.ToUInt64()*size;s.CommitLimit=performance.CommitLimit.ToUInt64()*size;s.Cache=performance.SystemCache.ToUInt64()*size;
            }
        }else s.Error+=" Memory information unavailable.";
        if((DateTime.UtcNow-slowUpdated).TotalSeconds>=10){
            try{
                var values=new List<PageInfo>();
                using(var search=new ManagementObjectSearcher("SELECT Name,AllocatedBaseSize,CurrentUsage,PeakUsage FROM Win32_PageFileUsage")){
                    search.Options.Timeout=TimeSpan.FromSeconds(3);
                    using(var results=search.Get())foreach(ManagementObject row in results)using(row)values.Add(new PageInfo{Name=Convert.ToString(row["Name"]),Total=Convert.ToDouble(row["AllocatedBaseSize"])*1048576,Used=Convert.ToDouble(row["CurrentUsage"])*1048576,Peak=Convert.ToDouble(row["PeakUsage"])*1048576});
                }
                pages=values.ToArray();pageError="";
            }catch(Exception){pageError="Page-file information unavailable; last readings may be stale.";}
            try{
                var values=new List<VolumeInfo>();
                foreach(var drive in DriveInfo.GetDrives())if(drive.DriveType==DriveType.Fixed && drive.IsReady)values.Add(new VolumeInfo{Name=drive.Name,Total=drive.TotalSize,Free=drive.TotalFreeSpace});
                volumes=values.ToArray();driveError="";
            }catch(Exception){driveError="Drive capacity information unavailable.";}
            if(cpuName.Length==0)using(var key=Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))cpuName=key==null?"Processor":Convert.ToString(key.GetValue("ProcessorNameString","Processor")).Trim();
            slowUpdated=DateTime.UtcNow;
        }
        s.Pages=pages;s.Volumes=volumes;s.CpuName=cpuName;s.PageError=pageError;s.DriveError=driveError;s.SlowTime=slowUpdated.ToLocalTime();
        s.Swap=pageError.Length>0?double.NaN:Percent(pages.Sum(p=>p.Used),pages.Sum(p=>p.Total));
        if(processes)s.Processes=Processes();else {processTimes.Clear();processUpdated=DateTime.MinValue;}
        s.Time=DateTime.Now;return s;
    }
    ProcessInfo[] Processes(){
        DateTime now=DateTime.UtcNow;double elapsed=(now-processUpdated).TotalSeconds;var values=new List<ProcessInfo>();var alive=new HashSet<int>();
        foreach(var process in Process.GetProcesses())using(process){
            try{
                if(process.Id==0)continue;
                var info=new ProcessInfo {Id=process.Id,Name=process.ProcessName,Memory=process.WorkingSet64,Cpu=double.NaN};
                long started=process.StartTime.ToUniversalTime().Ticks;double total=process.TotalProcessorTime.TotalSeconds;Tuple<long,double> old;
                if(elapsed>0 && processTimes.TryGetValue(info.Id,out old) && old.Item1==started)info.Cpu=Percent(Math.Max(0,total-old.Item2),elapsed*Environment.ProcessorCount);
                processTimes[info.Id]=Tuple.Create(started,total);alive.Add(info.Id);values.Add(info);
            }catch(System.ComponentModel.Win32Exception){}catch(InvalidOperationException){}catch(NotSupportedException){}
        }
        foreach(int id in processTimes.Keys.Where(id=>!alive.Contains(id)).ToArray())processTimes.Remove(id);
        processUpdated=now;return values.ToArray();
    }
    public void Dispose(){if(counters!=null)counters.Dispose();}
}
