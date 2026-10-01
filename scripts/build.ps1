param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$taskRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { $OutputDirectory = Join-Path $taskRoot 'dist' }
$taskSource = Join-Path $taskRoot 'assets\app'
$taskFramework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$taskCompiler = Join-Path $taskFramework 'csc.exe'
if (-not (Test-Path -LiteralPath $taskCompiler)) { throw 'Babel Tower requires Windows x64 with .NET Framework 4.8.' }
$taskOutput = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $taskOutput -Force | Out-Null
$taskExecutable = Join-Path $taskOutput 'BabelTower.exe'
$taskArguments = @('/nologo', '/target:winexe', '/platform:x64', '/langversion:5', '/optimize+', '/codepage:65001', "/out:$taskExecutable", "/win32manifest:$taskSource\app.manifest",
    '/r:System.dll', '/r:System.Core.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll', '/r:System.Net.Http.dll', '/r:System.Web.Extensions.dll',
    "/r:$taskFramework\WPF\UIAutomationClient.dll", "/r:$taskFramework\WPF\UIAutomationTypes.dll", "/r:$taskFramework\WPF\WindowsBase.dll",
    "$taskSource\Program.cs", "$taskSource\InlineOverlay.cs", "$taskSource\SelectionReader.cs", "$taskSource\TranslationEngine.cs", "$taskSource\SelectionImageReader.cs", "$taskSource\LocalSelectionOcr.cs")
& $taskCompiler @taskArguments
if ($LASTEXITCODE -ne 0) { throw 'Babel Tower compilation failed.' }
Get-Item -LiteralPath $taskExecutable | Select-Object FullName,Length
