# Collects the real-speech comparison set from the bench data folder (windows/INSTALL-TEST.md, part B):
#   powershell -NoProfile -ExecutionPolicy Bypass -File collect.ps1
# Pairs the kept recordings with scripts.tsv in the order they were dictated and writes, under
# %LOCALAPPDATA%\Murmur-bench\export: <script id>.wav, manifest.json (the scripts' text, the reference) and timings.json
# (seconds only). It never copies History text. The WAVs are your voice: they leave this PC only if you copy them.
$ErrorActionPreference = 'Stop'
$root = Join-Path $env:LOCALAPPDATA 'Murmur-bench'
$scripts = Get-Content (Join-Path $PSScriptRoot 'scripts.tsv') | Where-Object { $_ } | ForEach-Object { $c = $_ -split "`t"; [pscustomobject]@{ Id = $c[0]; Spoken = $c[2] } }
$entries = Get-ChildItem (Join-Path $root 'history') -Filter *.json | ForEach-Object { Get-Content $_.FullName -Raw | ConvertFrom-Json } |
    Where-Object { Test-Path (Join-Path $root "diagnostics\recordings\$($_.id).wav") } | Sort-Object recordedAt
if ($entries.Count -ne $scripts.Count) { throw "Found $($entries.Count) kept recordings, expected $($scripts.Count): dictate each script once, in order, then run this again (delete $root to start over)." }
$out = Join-Path $root 'export'
New-Item -ItemType Directory -Force $out | Out-Null
$manifest = @(); $timings = @()
for ($i = 0; $i -lt $scripts.Count; $i++) {
    $s = $scripts[$i]; $e = $entries[$i]; $wav = "$($s.Id).wav"
    Copy-Item (Join-Path $root "diagnostics\recordings\$($e.id).wav") (Join-Path $out $wav) -Force
    $manifest += [ordered]@{ id = $s.Id; wav = $wav; spoken = $s.Spoken; seconds = $e.seconds }
    $timings += [ordered]@{ wav = $wav; seconds = $e.seconds; transcription = $e.timing.transcription; cleanup = $e.timing.cleanup; route = $e.transcriptionProvider }
}
[IO.File]::WriteAllText((Join-Path $out 'manifest.json'), (ConvertTo-Json @($manifest) -Depth 3), (New-Object Text.UTF8Encoding $false))
[IO.File]::WriteAllText((Join-Path $out 'timings.json'), (ConvertTo-Json @($timings) -Depth 3), (New-Object Text.UTF8Encoding $false))
Write-Host "Wrote $($scripts.Count) clips to $out"
