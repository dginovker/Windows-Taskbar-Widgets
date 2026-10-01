# Windows 11

Run `./install.ps1` in PowerShell. Python 3.10+ is detected automatically; override with `-Python C:\path\python.exe`. Uses Windows' .NET Framework compiler and existing Claude/Codex logins. Installs into `%LOCALAPPDATA%\Programs\AIUsageRings`; rerun to update. `-NoStartup` disables sign-in startup.

The transparent taskbar rings open a usage flyout. Outer ring: week; inner: five hours; centre: days until reset. Right-click **Position and monitor** to choose a display and offset. Flyouts support Tab/Enter, scrolling, Escape, and outside-click dismissal.

This is an overlay for horizontal taskbars: leave space clear of Start, pinned apps, and Widgets. Secondary taskbars must be enabled in Windows. Hidden/full-screen taskbars hide the overlay. Only Claude/Codex are shown; KDE retains every provider. API-equivalent costs use the repository price table, not invoices.

Run `./uninstall.ps1` to remove the app and shortcuts; history/preferences remain in LocalAppData. No Explorer patches or admin privileges are required.

Starts, exits, refreshes and errors are logged to `%LOCALAPPDATA%\AIUsageRings\widget.log`. A run whose last line is neither `Process exiting` nor a session end was killed by another program.

Validation: `./test.ps1`, `python ./test_collector.py`. Geometry tests cover multiple DPI scales, negative monitor coordinates, narrow screens, and auto-hide; interactive flyout behavior also needs desktop testing.
