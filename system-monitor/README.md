# System Monitor for Windows

Run `install.ps1` in PowerShell. Installs a taskbar widget and sign-in shortcut; rerun to update. `uninstall.ps1` removes it. Source builds with Windows' .NET Framework compiler, without extra packages.

Four adjacent bars show CPU, RAM, Disk, and Swap, in that order. Click for one compact summary of all four readings and a Task Manager shortcut. Escape or an outside click dismisses it. Right-click for position/monitor settings, storage-versus-activity mode, or Exit.

The default position sits after the AI rings. This is a horizontal taskbar overlay, so leave room clear of pinned applications. A tray fallback appears when the overlay is hidden.

Samples once per second in a background worker; drive capacity and page-file allocation refresh every ten seconds. CPU is elapsed processor time; RAM excludes available memory. Swap uses actual page-file usage. Disk storage combines fixed drives. Unavailable readings show `--` in the summary.

Uses the taskbar/flyout code from dginovker's Click Analytics Windows port. `build.ps1 -Test` builds the live metric checks.
