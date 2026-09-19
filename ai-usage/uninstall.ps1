. (Join-Path $PSScriptRoot 'lifecycle.ps1')
$installDirectory = Join-Path $env:LOCALAPPDATA 'Programs\AIUsageRings'
$target = Join-Path $installDirectory 'AIUsageRings.exe'
Stop-Widget 'AIUsageRings' @($target, (Join-Path $PSScriptRoot 'AIUsageRings.exe'))
$shell = New-Object -ComObject WScript.Shell
foreach ($folder in @([Environment]::GetFolderPath('Startup'), [Environment]::GetFolderPath('Programs'))) {
    $link = Join-Path $folder 'AI Usage Rings.lnk'
    if (Test-Path -LiteralPath $link) {
        $shortcut = $shell.CreateShortcut($link)
        if ($shortcut.TargetPath -eq $target) { Remove-Item -LiteralPath $link }
    }
}
# Remove only installer-owned files; retain counts, usage caches, and preferences.
foreach ($name in @('AIUsageRings.exe','python-path.txt','widget_snapshot.py','uninstall.ps1','lifecycle.ps1')) {
    $file = Join-Path $installDirectory $name
    if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file }
}
Write-Output 'Uninstalled AI Usage Rings. Local history and preferences were retained.'
