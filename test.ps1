param([string]$Python = 'python', [switch]$LiveMetrics)
$ErrorActionPreference = 'Stop'
foreach ($widget in 'ai-usage','click-analytics') {
    & (Join-Path $PSScriptRoot "$widget\test.ps1")
}
& $Python (Join-Path $PSScriptRoot 'ai-usage\test_collector.py')
if ($LASTEXITCODE -ne 0) { throw 'Collector tests failed.' }
if ($LiveMetrics) {
    $test = & (Join-Path $PSScriptRoot 'system-monitor\build.ps1') -Test
    & $test
    if ($LASTEXITCODE -ne 0) { throw 'Live system metrics tests failed.' }
}
