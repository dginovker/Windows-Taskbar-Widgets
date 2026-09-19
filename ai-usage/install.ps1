param([string]$Python, [switch]$NoStart, [switch]$NoStartup)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lifecycle.ps1')
$installDirectory = Join-Path $env:LOCALAPPDATA 'Programs\AIUsageRings'
$target = Join-Path $installDirectory 'AIUsageRings.exe'
# Persist the actual interpreter, not a Store execution alias or launcher.
$candidates = @()
if ($Python) { $candidates += $Python } else {
    foreach ($command in 'python.exe','python3.exe','py.exe') {
        $found = Get-Command $command -ErrorAction SilentlyContinue
        if ($found -and $found.Source -notlike '*\WindowsApps\*') { $candidates += $found.Source }
    }
    foreach ($key in 'HKCU:\Software\Python\PythonCore','HKLM:\Software\Python\PythonCore') {
        if (Test-Path -LiteralPath $key) {
            foreach ($version in Get-ChildItem -LiteralPath $key) {
                $install = Get-ItemProperty -LiteralPath ($version.PSPath + '\InstallPath') -ErrorAction SilentlyContinue
                if ($install.ExecutablePath) { $candidates += $install.ExecutablePath }
            }
        }
    }
    $candidates += @(Get-ChildItem "$env:LOCALAPPDATA\Programs\Python\Python*\python.exe" -ErrorAction SilentlyContinue | ForEach-Object FullName)
}
$interpreter = $null
foreach ($candidate in $candidates | Select-Object -Unique) {
    if (-not (Test-Path -LiteralPath $candidate)) { continue }
    $probe = & $candidate -c 'import sys,sqlite3; assert sys.version_info >= (3,10); print(sys.executable)' 2>$null
    if ($LASTEXITCODE -eq 0 -and $probe -and (Test-Path -LiteralPath ([string]$probe))) { $interpreter = [string]$probe; break }
}
if (-not $interpreter) { throw 'Python 3.10+ with sqlite3 is required. Install Python or pass -Python C:\path\python.exe.' }
$built = & (Join-Path $PSScriptRoot 'build.ps1')
Stop-Widget 'AIUsageRings' @($target, (Join-Path $PSScriptRoot 'AIUsageRings.exe'), $built)
New-Item -ItemType Directory -Path $installDirectory -Force | Out-Null
Copy-Item -LiteralPath $built -Destination $target -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'uninstall.ps1') -Destination $installDirectory -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'lifecycle.ps1') -Destination $installDirectory -Force
[IO.File]::WriteAllText((Join-Path $installDirectory 'python-path.txt'), $interpreter)
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'widget_snapshot.py') -Destination $installDirectory -Force
$shell = New-Object -ComObject WScript.Shell
$startupLink = Join-Path ([Environment]::GetFolderPath('Startup')) 'AI Usage Rings.lnk'
$folders = @([Environment]::GetFolderPath('Programs'))
if (-not $NoStartup) { $folders += [Environment]::GetFolderPath('Startup') }
elseif (Test-Path -LiteralPath $startupLink) { Remove-Item -LiteralPath $startupLink }
foreach ($folder in $folders) {
    $shortcut = $shell.CreateShortcut((Join-Path $folder 'AI Usage Rings.lnk'))
    $shortcut.TargetPath = $target
    $shortcut.WorkingDirectory = $installDirectory
    $shortcut.Save()
}
if (-not $NoStart) { Start-Process -FilePath $target -WindowStyle Hidden }
Write-Output "Installed $target"
