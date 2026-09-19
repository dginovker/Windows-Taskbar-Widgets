param([string]$OutputDirectory = (Join-Path $PSScriptRoot '.build'), [switch]$Test)
$ErrorActionPreference = 'Stop'
$compiler = @("$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe", "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe") | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $compiler) { throw 'The Windows .NET Framework C# compiler is required.' }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$output = Join-Path $OutputDirectory $(if ($Test) { 'WindowsTests.exe' } else { 'ClickAnalytics.exe' })
$sources = @(Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.cs' | Where-Object { $Test -or $_.Name -ne 'WindowsTests.cs' } | ForEach-Object FullName)
$arguments = @('/nologo', '/warnaserror', "/out:$output", '/reference:System.Windows.Forms.dll', '/reference:System.Drawing.dll', '/reference:System.Web.Extensions.dll', '/reference:System.Core.dll', "/win32manifest:$PSScriptRoot\app.manifest")
if ($Test) { $arguments += @('/target:exe', '/main:WindowsTests') } else { $arguments += '/target:winexe' }
& $compiler @arguments @sources
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed.' }
Write-Output $output
