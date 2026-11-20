<#
 Local install -> delta update -> uninstall check against a folder feed. Run on a disposable Windows machine, in the logged-in session
 (the installed app starts). Phases run one at a time so the caller can watch:
   -Phase seed|install|launch|update|uninstall|status      -Setup <Setup.exe of the OLD version>  -Feed <folder with the new full + delta + releases.win.json>
 Seeds fake files in %LOCALAPPDATA%\Murmur and prints SHA-256 prefixes of every file (the app may move an invalid History seed into History\unreadable under a new name; compare hashes). Writes to $Log; prints no user content.
#>
param([Parameter(Mandatory)][ValidateSet('seed','install','launch','update','uninstall','status')][string]$Phase,
      [string]$Setup = 'C:\src\w-m6\setup1\MurmurApp-win-Setup.exe', [string]$Feed = 'C:\src\w-m6\releases', [string]$Log = 'C:\src\w-m6\flow.log')
$data = Join-Path $env:LOCALAPPDATA 'Murmur'; $app = Join-Path $env:LOCALAPPDATA 'MurmurApp'
function Say($m) { "$(Get-Date -f HH:mm:ss) $m" | Tee-Object -FilePath $Log -Append }
function Hashes { Get-ChildItem $data -Recurse -File | ? { $_.FullName -notmatch 'diagnostics|\.lock' } | Sort FullName | % { "$($_.FullName.Substring($data.Length)) $((Get-FileHash $_.FullName).Hash.Substring(0,12))" } }
function State {
    $exe = Join-Path $app 'current\Murmur.exe'
    Say ("installed exe: " + $(if (Test-Path $exe) { (Get-Item $exe).VersionInfo.ProductVersion } else { 'none' }))
    Say ("current folder: " + $(if (Test-Path $app) { (Get-ChildItem $app -Name) -join ', ' } else { 'no MurmurApp folder' }))
    Say ("Run value Murmur: " + $(if ((Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -ErrorAction SilentlyContinue).Murmur) { 'present' } else { 'absent' }))
    Say ("running: " + (@(Get-Process Murmur -ErrorAction SilentlyContinue).Count))
    Say ("seed files: " + ((Hashes) -join '; '))
}
switch ($Phase) {
    'seed' {
        New-Item -ItemType Directory -Force "$data\History" | Out-Null
        '{"updateChecksEnabled":true,"seedMarker":"m6"}' | Set-Content "$data\settings.json" -NoNewline
        'seed history entry' | Set-Content "$data\History\seed-entry.json" -NoNewline
        Copy-Item "$data\settings.json" "$data\seed-settings-copy.json"
        Say 'seeded'; State
    }
    'install' {
        $env:MURMUR_UPDATE_FEED = $Feed; $env:MURMUR_TEST_EVENTS = '1'   # inherited by the app Setup starts
        Start-Process $Setup -ArgumentList '--silent' -Wait
        Start-Sleep 25; State
    }
    'launch' {
        # Silent setup does not start the app; start it through the stable launcher like the Run entry would.
        $env:MURMUR_UPDATE_FEED = $Feed; $env:MURMUR_TEST_EVENTS = '1'
        Start-Process "$app\Murmur.exe"
        Start-Sleep 40; State
        Say ("packages: " + ((Get-ChildItem "$app\packages" -Name -ErrorAction SilentlyContinue) -join ', '))
    }
    'update' {
        # The app downloaded the delta at launch; apply it the way Restart to update would (Update.exe is Velopack's launcher).
        Say ("packages: " + ((Get-ChildItem "$app\packages" -Name -ErrorAction SilentlyContinue) -join ', '))
        Say ("update-ready event: " + [bool](Select-String -Path "$data\diagnostics\events.jsonl" -Pattern 'update-ready' -Quiet -ErrorAction SilentlyContinue))
        $env:MURMUR_UPDATE_FEED = $Feed; $env:MURMUR_TEST_EVENTS = '1'
        & "$app\Update.exe" apply --waitPid (Get-Process Murmur).Id
        Start-Sleep 20; State
    }
    'uninstall' {
        Get-Process Murmur -ErrorAction SilentlyContinue | Stop-Process -Force
        & "$app\Update.exe" --uninstall --silent | Out-Null
        Start-Sleep 10; State
    }
    'status' { State }
}
