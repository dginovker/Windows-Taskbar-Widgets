$ErrorActionPreference = 'Stop'
$test = & (Join-Path $PSScriptRoot 'build.ps1') -Test
& $test
if ($LASTEXITCODE -ne 0) { throw 'Windows tests failed.' }
