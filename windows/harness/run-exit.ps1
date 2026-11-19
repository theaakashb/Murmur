# M2 exit harness, one command, run on the Windows VM from an SSH (session 0) shell:
#   powershell -NoProfile -ExecutionPolicy Bypass -File <checkout>\windows\harness\run-exit.ps1
# Builds the app, the fake provider and the driver; starts the fake provider; launches Murmur.exe and the driver in the
# console session (session 1) through scheduled tasks; collects results.json + summary.txt + screenshots in
# C:\src\harness-run-<timestamp>; cleans up. Re-runnable: every run gets a fresh data dir and run folder.
# Exit code 0 only when every dictation passes every check that was verifiable.
param(
    [string]$OutBase = 'C:\src',
    [int]$Count = 20,
    [switch]$SkipAppBuild,   # also implied by -AppExe
    [string]$AppExe = '',
    [switch]$LocalAsr,       # transcribe with the local speech helper instead of the fake provider; cleanup still uses the fake. Needs -ModelDir and a published Murmur.Speech
    [string]$ModelDir = '',  # folder with encoder/decoder/joiner .int8.onnx and tokens.txt (used with -LocalAsr)
    [string]$SpeechHelper = '',  # path to MurmurSpeech.exe (used with -LocalAsr)
    [switch]$LocalOnly,      # key-free: local transcription AND local cleanup, no fake-provider calls. Side-loads the pinned models from -ModelSource into the run's data\Models
    [switch]$SeedDictionary, # with -LocalOnly: also write a dictionary (the fixtures' names plus the ordinary word "quarterly") so the speech helper runs with --vocabulary
    [string]$ModelSource = 'C:\src\models'  # speech\ (4 pinned files) and cleanup\ (the pinned GGUF); never committed
)
$ErrorActionPreference = 'Stop'
# Keep the caller's PATH first (a dotnet outside Program Files), then add the saved machine and user paths for SSH sessions.
$env:Path = ($env:Path, [Environment]::GetEnvironmentVariable('Path', 'Machine'), [Environment]::GetEnvironmentVariable('Path', 'User')) -join ';'
$dotnetRoot = if ($env:DOTNET_ROOT) { "set DOTNET_ROOT=$env:DOTNET_ROOT" } else { 'rem default .NET location' }
$harness = $PSScriptRoot
$windows = Split-Path $harness -Parent
$ts = Get-Date -Format 'yyyyMMdd-HHmmss'
$run = Join-Path $OutBase "harness-run-$ts"
$data = Join-Path $run 'data'
$synth = Join-Path $data 'synthetic'
New-Item -ItemType Directory -Force $data, $synth | Out-Null
Write-Host "Run folder: $run"

function Stop-Leftovers {
    # Only our own artefacts: processes from earlier harness runs (by name) and anything started from a harness-run folder.
    Get-Process Murmur, FakeProvider, Driver -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -like '*harness-run-*' -and $_.ProcessId -ne $PID } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
}
function Start-InSession1([string]$Name, [string]$Exe, [string]$Arguments) {
    $a = New-ScheduledTaskAction -Execute $Exe -Argument $Arguments
    $p = New-ScheduledTaskPrincipal -UserId (whoami) -LogonType Interactive -RunLevel Limited
    $s = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Hours 1) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
    Register-ScheduledTask -TaskName $Name -Action $a -Principal $p -Settings $s -Force | Out-Null
    Start-ScheduledTask -TaskName $Name
}
function Remove-Task([string]$Name) {
    Stop-ScheduledTask -TaskName $Name -ErrorAction SilentlyContinue
    Unregister-ScheduledTask -TaskName $Name -Confirm:$false -ErrorAction SilentlyContinue
}

$fake = $null; $driverExit = 1
try {
    Stop-Leftovers
    Write-Host 'Building...'
    $projects = @("$harness\FakeProvider\FakeProvider.csproj", "$harness\Driver\Driver.csproj")
    if (-not $SkipAppBuild -and -not $AppExe) { $projects = @("$windows\Murmur.App.slnx") + $projects }
    foreach ($proj in $projects) {
        dotnet build $proj -c Release --nologo -v q | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "build failed: $proj" }
    }
    if (-not $AppExe) {
        $AppExe = (Get-ChildItem "$windows\src\Murmur.App\bin" -Recurse -Filter Murmur.exe -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -like '*Release*' } | Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
    }
    if (-not $AppExe -or -not (Test-Path $AppExe)) { throw 'Murmur.exe not found; pass -AppExe' }
    $driverExe = "$harness\Driver\bin\Release\net10.0-windows10.0.19041.0\Driver.exe"
    $fakeDll = "$harness\FakeProvider\bin\Release\net10.0\FakeProvider.dll"
    Write-Host "App: $AppExe"

    # Fake provider on a loopback port chosen by the OS.
    $portFile = Join-Path $run 'provider-url.txt'
    $fake = Start-Process dotnet -ArgumentList "`"$fakeDll`" --manifest `"$harness\fixtures\manifest.json`" --port-file `"$portFile`"" `
        -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $run 'provider.log')
    for ($i = 0; $i -lt 100 -and -not (Test-Path $portFile); $i++) { Start-Sleep -Milliseconds 200 }
    if (-not (Test-Path $portFile)) { throw 'fake provider did not start' }
    $url = (Get-Content $portFile -Raw).Trim()
    Write-Host "Fake provider: $url"

    # settings.json: Custom Provider on the fake for transcription and cleanup; Polish on so cleanup uses the model route.
    $settings = [ordered]@{
        transcriptionRoute = 'cloud'; cleanupChoice = 'custom-api'; cleanupPolish = $true
        customAPIBaseURL = "$url/v1"; customTranscriptionAPIBaseURL = "$url/v1"
        customAPIModel = 'fake-cleanup'; customTranscriptionModel = 'fake-asr'; customAPIReasoningEffort = ''
        automaticInsertion = $true; historyMode = 'on'; shortcut = 'right-ctrl'
    }
    if ($LocalOnly) {
        # Folder names are ModelFacts.Directory: <repo name>-<rev7> under <data root>\Models. Only the pinned files are copied; the app verifies them against the pins.
        $models = Join-Path $data 'Models'
        $speechDir = Join-Path $models 'sherpa-onnx-nemo-parakeet-unified-en-0.6b-int8-non-streaming-8c3a10f'
        $cleanupDir = Join-Path $models 'vedu-scribe-0.8b-edf2759'
        New-Item -ItemType Directory -Force $speechDir, $cleanupDir | Out-Null
        foreach ($f in 'encoder.int8.onnx', 'decoder.int8.onnx', 'joiner.int8.onnx', 'tokens.txt') { Copy-Item (Join-Path $ModelSource "speech\$f") $speechDir }
        Copy-Item (Join-Path $ModelSource 'cleanup\vedu-scribe-0.8b-Q8_0.gguf') $cleanupDir
        $settings.transcriptionRoute = 'local'; $settings.cleanupChoice = 'local-model'
        foreach ($k in 'customAPIBaseURL', 'customTranscriptionAPIBaseURL', 'customAPIModel', 'customTranscriptionModel', 'customAPIReasoningEffort') { $settings.Remove($k) }
    }
    if ($SeedDictionary) {
        $dict = Join-Path $data 'dictionary'; New-Item -ItemType Directory -Force $dict | Out-Null
        $entries = foreach ($t in 'Maria Gonzalez', 'Peter Nakamura', 'Oliver Brennan', 'Priya Raman', 'Lakeside Cafe', 'quarterly') {
            [ordered]@{ createdAt = 800000000.0; enabled = $true; id = [guid]::NewGuid().ToString().ToUpper(); preferred = $t; source = 'manual'; variants = @() } }
        # No BOM: the app's JSON reader rejects one.
        [IO.File]::WriteAllText((Join-Path $dict 'dictionary.json'), (@{ version = 1; entries = @($entries) } | ConvertTo-Json -Depth 5), (New-Object Text.UTF8Encoding $false))
    }
    if ($LocalAsr) {
        if (-not (Test-Path (Join-Path $ModelDir 'encoder.int8.onnx')) -or -not (Test-Path $SpeechHelper)) { throw '-LocalAsr needs -ModelDir (model folder) and -SpeechHelper (MurmurSpeech.exe)' }
        $settings.transcriptionRoute = 'local'; $settings.modelDirectory = $ModelDir
    }
    $settings | ConvertTo-Json | Set-Content (Join-Path $data 'settings.json') -Encoding UTF8
    # An elevated SSH session creates the run folder owned by Administrators; the unelevated app must own its data folder to make it private.
    & icacls $run /setowner (whoami) /T /C /Q | Out-Null

    # Launcher: the env vars are the only thing that makes this a test run.
    $launcher = Join-Path $run 'launch-app.cmd'
    @(
        '@echo off'
        "set MURMUR_DATA_DIR=$data"
        "set MURMUR_SYNTHETIC_AUDIO=$synth"
        'set MURMUR_TEST_EVENTS=1'
        $(if ($LocalAsr) { "set MURMUR_SPEECH_HELPER=$SpeechHelper" })
        "`"$AppExe`""
    ) | Set-Content $launcher -Encoding ASCII
    Start-InSession1 'MurmurHarnessApp' 'cmd.exe' "/c `"$launcher`""

    # Driver config and run.
    $cfg = Join-Path $run 'driver-config.json'
    @{ dataDir = $data; outDir = $run; providerUrl = $url; fixturesDir = "$harness\fixtures"; pageFile = "$harness\pages\textarea.html"; count = $Count } |
        ConvertTo-Json | Set-Content $cfg -Encoding UTF8
    $driverCmd = Join-Path $run 'run-driver.cmd'
    @('@echo off', $dotnetRoot, "`"$driverExe`" run `"$cfg`" > `"$run\driver.log`" 2>&1", "echo %ERRORLEVEL% > `"$run\driver-exit.txt`"") | Set-Content $driverCmd -Encoding ASCII
    Start-InSession1 'MurmurHarnessDriver' 'cmd.exe' "/c `"$driverCmd`""
    $deadline = (Get-Date).AddSeconds(150 + 45 * $Count)
    while (-not (Test-Path "$run\driver-exit.txt") -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 2 }
    if (Test-Path "$run\driver-exit.txt") { $driverExit = [int](Get-Content "$run\driver-exit.txt" -Raw).Trim() } else { Write-Host 'Driver timed out.' }

    # Provider-side evidence (content-free log).
    try {
        $log = Invoke-RestMethod "$url/control/requests"
        $log | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $run 'provider-requests.json') -Encoding UTF8
        $tr = @($log | Where-Object { $_.path -eq '/v1/audio/transcriptions' })
        $ch = @($log | Where-Object { $_.path -eq '/v1/chat/completions' })
        $empty = @($tr | Where-Object { $_.audioBytes -le 0 }).Count
        $line = "Provider: $($tr.Count) transcription and $($ch.Count) cleanup requests; $empty transcription requests had no audio."
        Add-Content "$run\summary.txt" "`n$line"; Write-Host $line
        # Local ASR: the provider must have seen no transcription request (the helper did that work) and one cleanup request per dictation.
        if ($LocalOnly) { if ($tr.Count -ne 0 -or $ch.Count -ne 0) { $driverExit = 1 } }
        elseif ($LocalAsr) { if ($tr.Count -ne 0 -or $ch.Count -lt $Count) { $driverExit = 1 } }
        elseif ($tr.Count -lt $Count -or $empty -gt 0) { $driverExit = 1 }
    } catch { Write-Host "Could not read provider log: $_"; $driverExit = 1 }
}
finally {
    Remove-Task 'MurmurHarnessDriver'; Remove-Task 'MurmurHarnessApp'
    if ($fake) { Stop-Process -Id $fake.Id -Force -ErrorAction SilentlyContinue }
    Stop-Leftovers
    # The side-loaded copies are 1.5 GB per run; the run keeps everything else.
    if ($LocalOnly) { Remove-Item (Join-Path $data 'Models') -Recurse -Force -ErrorAction SilentlyContinue }
}
Write-Host "Results: $run\results.json   Summary: $run\summary.txt   Screenshots: $run\screenshot-*.png"
if (Test-Path "$run\summary.txt") { Get-Content "$run\summary.txt" | Select-Object -First 12 | Out-Host }
exit $driverExit
