# Windows 11

Run `./install.ps1` in PowerShell. Uses Windows' .NET Framework compiler and installs into `%LOCALAPPDATA%\Programs\ClickAnalytics`; rerun to update. `-NoStartup` disables sign-in startup. Run `./uninstall.ps1` to remove the app and shortcuts while retaining history.

Counts and graphs open from the taskbar or tray. Right-click **Position and monitor** to choose a display and offset. This is an overlay for horizontal taskbars: leave space clear of Start, pinned apps, and Widgets. Secondary taskbars must be enabled in Windows.

The popup follows KDE: lifetime totals, button bars, scroll/travel, and both hourly graphs with hover details. Counts are saved every ten seconds and on normal exit to `%LOCALAPPDATA%\ClickAnalytics\counts.json`, with an atomic backup. No typed text or key identities are stored. Pointer travel estimates screen distance at 96 pixels/inch, not physical mouse travel; wheel/touchpad scrolling is combined. Network totals use one routed IPv4 interface (possibly a VPN), excluding downtime. Secure-desktop input and KDE SQLite import are unsupported.

Run `./test.ps1` for persistence and multi-monitor/DPI geometry checks. Test live input, flyout dismissal, and monitor changes on an interactive desktop before release.
