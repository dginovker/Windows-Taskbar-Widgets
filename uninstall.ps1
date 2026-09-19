param(
    [ValidateSet('ai-usage','click-analytics','system-monitor')]
    [string[]]$Widgets = @('ai-usage','click-analytics','system-monitor')
)
$ErrorActionPreference = 'Stop'
foreach ($widget in $Widgets) {
    & (Join-Path $PSScriptRoot "$widget\uninstall.ps1")
}
