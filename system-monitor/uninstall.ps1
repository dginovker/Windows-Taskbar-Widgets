. (Join-Path $PSScriptRoot 'lifecycle.ps1')
$installDirectory = Join-Path $env:LOCALAPPDATA 'Programs\SystemMonitor'
$target = Join-Path $installDirectory 'SystemMonitor.exe'
Stop-Widget 'SystemMonitor' @($target, (Join-Path $PSScriptRoot 'SystemMonitor.exe'))
$shell = New-Object -ComObject WScript.Shell
foreach ($folder in @([Environment]::GetFolderPath('Startup'), [Environment]::GetFolderPath('Programs'))) {
    $link = Join-Path $folder 'System Monitor.lnk'
    if (Test-Path -LiteralPath $link) {
        $shortcut = $shell.CreateShortcut($link)
        if ($shortcut.TargetPath -eq $target) { Remove-Item -LiteralPath $link }
    }
}
# Remove only installer-owned files; retain counts, usage caches, and preferences.
foreach ($name in @('SystemMonitor.exe','python-path.txt','widget_snapshot.py','uninstall.ps1','lifecycle.ps1')) {
    $file = Join-Path $installDirectory $name
    if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file }
}
Write-Output 'Uninstalled System Monitor. Local history and preferences were retained.'
