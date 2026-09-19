param(
    [ValidateSet('ai-usage','click-analytics','system-monitor')]
    [string[]]$Widgets = @('ai-usage','click-analytics','system-monitor'),
    [string]$Python,
    [switch]$NoStart,
    [switch]$NoStartup
)
$ErrorActionPreference = 'Stop'
foreach ($widget in $Widgets) {
    $options = @{ NoStart = $NoStart; NoStartup = $NoStartup }
    if ($widget -eq 'ai-usage' -and $Python) { $options.Python = $Python }
    & (Join-Path $PSScriptRoot "$widget\install.ps1") @options
}
