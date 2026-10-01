param([string]$ModelPath, [string]$ModelDirectory)
$ErrorActionPreference = 'Stop'
$taskExpectedHash = 'dc5f44fcf1fa496ee7ad725982c0c8c553a4de00259b53af84c4b89fb0c06699'
$taskModelName = 'swipetranslate-hymt2'
$taskFileName = 'Hy-MT2-1.8B-Q4_K_M.gguf'
$taskDownloadUrl = 'https://huggingface.co/tencent/Hy-MT2-1.8B-GGUF/resolve/main/Hy-MT2-1.8B-Q4_K_M.gguf?download=true'
$taskOllama = Get-Command ollama.exe -ErrorAction SilentlyContinue
if ($taskOllama) { $taskOllamaExe = $taskOllama.Source }
else { $taskOllamaExe = Join-Path $env:LOCALAPPDATA 'Programs\Ollama\ollama.exe' }
if (-not (Test-Path -LiteralPath $taskOllamaExe)) { throw 'Install and start Ollama from https://ollama.com/download/windows first.' }
if ([string]::IsNullOrWhiteSpace($ModelDirectory)) { $ModelDirectory = Join-Path $env:LOCALAPPDATA 'BabelTower\models' }
if ([string]::IsNullOrWhiteSpace($ModelPath)) {
    New-Item -ItemType Directory -Path $ModelDirectory -Force | Out-Null
    $ModelPath = Join-Path $ModelDirectory $taskFileName
    if (-not (Test-Path -LiteralPath $ModelPath)) {
        $taskPartial = $ModelPath + '.download'
        Write-Host 'Downloading the official Hy-MT2 Q4_K_M weights. Translation itself remains local.'
        [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -Uri $taskDownloadUrl -OutFile $taskPartial -UseBasicParsing
        $taskDownloadHash = (Get-FileHash -LiteralPath $taskPartial -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($taskDownloadHash -ne $taskExpectedHash) { throw 'Downloaded model SHA256 does not match. The file has not been imported; inspect the official model release before retrying.' }
        $taskDirectoryAbsolute = [System.IO.Path]::GetFullPath($ModelDirectory).TrimEnd('\')
        $taskPartialAbsolute = [System.IO.Path]::GetFullPath($taskPartial)
        $taskDestinationAbsolute = [System.IO.Path]::GetFullPath($ModelPath)
        if (-not $taskPartialAbsolute.StartsWith($taskDirectoryAbsolute + '\', [StringComparison]::OrdinalIgnoreCase) -or
            -not $taskDestinationAbsolute.StartsWith($taskDirectoryAbsolute + '\', [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Model download path is outside the selected model directory.'
        }
        Move-Item -LiteralPath $taskPartialAbsolute -Destination $taskDestinationAbsolute
    }
}
$taskResolvedModel = (Resolve-Path -LiteralPath $ModelPath).Path
$taskHash = (Get-FileHash -LiteralPath $taskResolvedModel -Algorithm SHA256).Hash.ToLowerInvariant()
if ($taskHash -ne $taskExpectedHash) { throw 'The supplied model SHA256 does not match the official Q4_K_M file. No model was imported.' }
# Ollama stores its imported blob separately. This script does not change the
# user's Ollama store, GPU settings, other models, startup items or permissions.
$taskModelfile = Join-Path ([System.IO.Path]::GetDirectoryName($taskResolvedModel)) 'BabelTower.Modelfile'
$taskOllamaPath = $taskResolvedModel.Replace('\', '/')
$taskContents = 'FROM "' + $taskOllamaPath + '"' + "`nPARAMETER num_ctx 8192`n"
[System.IO.File]::WriteAllText($taskModelfile, $taskContents, (New-Object System.Text.UTF8Encoding($false)))
$taskPreviousOllamaHost = $env:OLLAMA_HOST
try {
    # Match the application's fixed loopback endpoint even when the user's
    # shell happens to target a different Ollama server.
    $env:OLLAMA_HOST = 'http://127.0.0.1:11434'
    & $taskOllamaExe create $taskModelName -f $taskModelfile
    if ($LASTEXITCODE -ne 0) { throw 'Ollama import failed. Start Ollama, check available disk space, and retry.' }
} finally { $env:OLLAMA_HOST = $taskPreviousOllamaHost }
[pscustomobject]@{ Model = $taskModelName; ModelPath = $taskResolvedModel; SHA256 = $taskHash; Imported = $true }
