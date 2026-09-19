$ErrorActionPreference = 'Stop'
if (-not ('WidgetLifecycle' -as [type])) {
    Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class WidgetLifecycle {
    delegate bool Callback(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] static extern bool EnumWindows(Callback callback, IntPtr parameter);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr window, uint message, IntPtr w, IntPtr l);
    public static void Stop(uint process) {
        EnumWindows(delegate(IntPtr window, IntPtr unused) {
            uint owner; GetWindowThreadProcessId(window, out owner);
            if (owner == process) PostMessage(window, 0x8031, IntPtr.Zero, IntPtr.Zero);
            return true;
        }, IntPtr.Zero);
    }
}
'@
}
function Stop-Widget([string]$Name, [string[]]$AllowedPaths) {
    foreach ($process in Get-Process -Name $Name -ErrorAction SilentlyContinue) {
        if ($AllowedPaths -notcontains $process.Path) { throw "Another $Name copy is running at $($process.Path). Exit it first." }
        [WidgetLifecycle]::Stop($process.Id)
        if (-not $process.WaitForExit(10000)) { throw "Exit $Name from its menu and try again; it did not acknowledge shutdown." }
    }
}
