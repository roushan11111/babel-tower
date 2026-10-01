param([switch]$Background, [switch]$CheckOnly)
$ErrorActionPreference = 'Stop'
$taskRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskExe = Join-Path $taskRoot 'dist\BabelTower.exe'
if (-not (Test-Path -LiteralPath $taskExe)) {
    if ($CheckOnly) { throw 'BabelTower.exe is missing. Run scripts/build.ps1 first.' }
    & (Join-Path $PSScriptRoot 'build.ps1')
}
$taskOllama = Get-Command ollama.exe -ErrorAction SilentlyContinue
if ($taskOllama) { $taskOllamaExe = $taskOllama.Source }
else { $taskOllamaExe = Join-Path $env:LOCALAPPDATA 'Programs\Ollama\ollama.exe' }
if (-not (Test-Path -LiteralPath $taskOllamaExe)) { throw 'Install Ollama from https://ollama.com/download/windows before starting Babel Tower.' }
if ($CheckOnly) {
    # CLI `show` may auto-start Ollama. Read loopback metadata directly instead:
    # check-only must never launch a process or load a model for inference.
    Add-Type -AssemblyName System.Net.Http
    $taskHandler = New-Object System.Net.Http.HttpClientHandler
    $taskHandler.UseProxy = $false
    $taskHandler.AllowAutoRedirect = $false
    $taskClient = New-Object System.Net.Http.HttpClient($taskHandler)
    $taskClient.Timeout = [TimeSpan]::FromSeconds(3)
    $taskContent = $null
    $taskVersionResponse = $null
    $taskShowResponse = $null
    try {
        $taskVersionResponse = $taskClient.GetAsync('http://127.0.0.1:11434/api/version').GetAwaiter().GetResult()
        if (-not $taskVersionResponse.IsSuccessStatusCode) { throw 'The local Ollama server is not ready.' }
        $taskContent = New-Object System.Net.Http.StringContent('{"model":"swipetranslate-hymt2"}', [Text.Encoding]::UTF8, 'application/json')
        $taskShowResponse = $taskClient.PostAsync('http://127.0.0.1:11434/api/show', $taskContent).GetAwaiter().GetResult()
        if (-not $taskShowResponse.IsSuccessStatusCode) { throw 'The local Hy-MT2 model is missing.' }
        $taskMetadata = $taskShowResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json
        $taskExpectedHash = 'dc5f44fcf1fa496ee7ad725982c0c8c553a4de00259b53af84c4b89fb0c06699'
        $taskFromLines = @($taskMetadata.modelfile -split '\r?\n' | Where-Object { $_ -cmatch '^FROM[ \t]+' })
        $taskBlobName = ''
        if ($taskFromLines.Count -eq 1) { $taskBlobName = [IO.Path]::GetFileName(($taskFromLines[0] -replace '^FROM[ \t]+', '').Trim().Trim('"')) }
        if ($taskBlobName -cne ('sha256-' + $taskExpectedHash)) {
            throw 'The local model alias does not refer to the verified official Hy-MT2 Q4_K_M weights. Run scripts/setup-model.ps1 with the official model.'
        }
        [pscustomobject]@{ Executable = $taskExe; Ollama = $taskOllamaExe; Endpoint = 'http://127.0.0.1:11434'; Model = 'swipetranslate-hymt2'; VerifiedWeights = $true; Ready = $true }
        return
    } catch { throw ('Local readiness check failed: ' + $_.Exception.Message + ' Start Ollama and run scripts/setup-model.ps1 when required.') }
    finally {
        if ($taskVersionResponse) { $taskVersionResponse.Dispose() }
        if ($taskShowResponse) { $taskShowResponse.Dispose() }
        if ($taskContent) { $taskContent.Dispose() }
        $taskClient.Dispose()
    }
}
# Do not silently run a second copy or kill the user's existing translator.
$taskHasMutex = $false
$taskMutex = $null
try { $taskMutex = [System.Threading.Mutex]::OpenExisting('Local\SwipeTranslate.Prototype.v1'); $taskHasMutex = $true }
catch [System.Threading.WaitHandleCannotBeOpenedException] { }
finally { if ($taskMutex) { $taskMutex.Dispose() } }
if ($taskHasMutex) { throw 'A Babel Tower / earlier selection translator is already running. Exit it from its tray menu, then run this script again.' }
# Normal launch uses the same verified local weights. The user opens Ollama
# first; a missing service or model produces an instruction rather than a
# download, a remote check, or a translator launched against the wrong model.
& $PSCommandPath -CheckOnly | Out-Null
$taskStart = @{ FilePath = $taskExe; WorkingDirectory = (Split-Path -Parent $taskExe); WindowStyle = 'Hidden'; PassThru = $true }
if ($Background) { $taskStart['ArgumentList'] = '--background' }
$taskProcess = Start-Process @taskStart
[pscustomobject]@{ Started = $true; ProcessId = $taskProcess.Id; Executable = $taskExe }
