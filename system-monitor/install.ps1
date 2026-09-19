param([switch]$NoStart, [switch]$NoStartup)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lifecycle.ps1')
$installDirectory = Join-Path $env:LOCALAPPDATA 'Programs\SystemMonitor'
$target = Join-Path $installDirectory 'SystemMonitor.exe'
$built = & (Join-Path $PSScriptRoot 'build.ps1')
Stop-Widget 'SystemMonitor' @($target, (Join-Path $PSScriptRoot 'SystemMonitor.exe'), $built)
New-Item -ItemType Directory -Path $installDirectory -Force | Out-Null
Copy-Item -LiteralPath $built -Destination $target -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'uninstall.ps1') -Destination $installDirectory -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'lifecycle.ps1') -Destination $installDirectory -Force
$shell = New-Object -ComObject WScript.Shell
$startupLink = Join-Path ([Environment]::GetFolderPath('Startup')) 'System Monitor.lnk'
$folders = @([Environment]::GetFolderPath('Programs'))
if (-not $NoStartup) { $folders += [Environment]::GetFolderPath('Startup') }
elseif (Test-Path -LiteralPath $startupLink) { Remove-Item -LiteralPath $startupLink }
foreach ($folder in $folders) {
    $shortcut = $shell.CreateShortcut((Join-Path $folder 'System Monitor.lnk'))
    $shortcut.TargetPath = $target
    $shortcut.WorkingDirectory = $installDirectory
    $shortcut.Save()
}
if (-not $NoStart) { Start-Process -FilePath $target -WindowStyle Hidden }
Write-Output "Installed $target"
