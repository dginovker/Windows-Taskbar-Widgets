$ErrorActionPreference = 'Stop'
foreach ($widget in 'ai-usage','click-analytics','system-monitor') {
    & (Join-Path $PSScriptRoot "$widget\build.ps1")
}
