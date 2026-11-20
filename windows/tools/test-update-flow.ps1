<#
 Local install -> delta update -> uninstall check against a folder feed. Run on a disposable Windows machine, in the logged-in session
 (the installed app starts). Phases run one at a time so the caller can watch:
   -Phase seed|install|launch|update|restart|uninstall|status      -Setup <Setup.exe of the OLD version>  -Feed <folder with the new full + delta + releases.win.json>
 'restart' exercises the real Updater.RestartToUpdate through Settings > Updates > Restart to Update (UI Automation Invoke, no input
 simulation): run it in the logged-in console session (e.g. via a scheduled task with an Interactive principal) after 'install'.
 Seeds fake files in %LOCALAPPDATA%\Murmur and prints SHA-256 prefixes of every file (the app may move an invalid History seed into History\unreadable under a new name; compare hashes). Writes to $Log; prints no user content.
#>
param([Parameter(Mandatory)][ValidateSet('seed','install','launch','update','restart','uninstall','status')][string]$Phase,
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
    'restart' {
        Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
        $A = [System.Windows.Automation.AutomationElement]; $UT = [System.Windows.Automation.TreeScope]
        # Murmur opens several top-level windows (Settings, Welcome): search all of this process's windows.
        function Find($pid_, $name) { foreach ($w in $A::RootElement.FindAll($UT::Children, (New-Object System.Windows.Automation.PropertyCondition($A::ProcessIdProperty, $pid_)))) { $e = $w.FindFirst($UT::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, $name))); if ($e) { return $e } } }
        function Wait($what, $secs, [scriptblock]$test) { $end = (Get-Date).AddSeconds($secs); while ((Get-Date) -lt $end) { $r = & $test; if ($r) { return $r }; Start-Sleep 1 }; Say "TIMEOUT waiting for $what" | Out-Null; $null }
        $events = "$data\diagnostics\events.jsonl"
        Get-Process Murmur -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep 2
        Remove-Item $events -ErrorAction SilentlyContinue
        $before = Hashes; $runBefore = (Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -ErrorAction SilentlyContinue).Murmur
        $env:MURMUR_UPDATE_FEED = $Feed; $env:MURMUR_TEST_EVENTS = '1'
        $startVersion = (Get-Item "$app\current\Murmur.exe").VersionInfo.ProductVersion
        $p = Start-Process "$app\current\Murmur.exe" -ArgumentList '--settings' -PassThru
        Say "started v$startVersion pid $($p.Id)"
        $ready = Wait 'update-ready' 120 { (Test-Path $events) -and (Select-String -Path $events -Pattern 'update-ready' -Quiet) }
        Say "update-ready: $([bool]$ready); packages: $((Get-ChildItem "$app\packages" -Name -ErrorAction SilentlyContinue) -join ', ')"
        $nav = Wait 'Updates nav item' 30 { Find $p.Id 'Updates' }
        if (-not $nav) { Say 'FAIL no Updates nav item'; return }
        if ($nav) { try { $nav.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() } catch { $nav.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() } }
        $btn = Wait 'Restart to Update button' 20 { Find $p.Id 'Restart to Update' }
        if (-not $btn) { Say 'FAIL no Restart to Update button'; return }
        $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Say 'invoked Restart to Update'
        $new = Wait 'restarted app' 90 { $q = Get-Process Murmur -ErrorAction SilentlyContinue | ? { $_.Id -ne $p.Id }; if ($q -and (Test-Path "$app\current\Murmur.exe") -and (Get-Item "$app\current\Murmur.exe").VersionInfo.ProductVersion -ne $startVersion) { $q } }
        Start-Sleep 5
        Say ("old pid alive: " + [bool](Get-Process -Id $p.Id -ErrorAction SilentlyContinue) + "; new process: " + [bool]$new)
        Say ("version now: " + (Get-Item "$app\current\Murmur.exe").VersionInfo.ProductVersion + " (was $startVersion)")
        # The running app legitimately rewrites settings.json and adds files; the untouched seed files must be byte-identical across the update.
        $after = Hashes; $seedKept = @($before | ? { $_ -match 'seed-' }) | ? { $after -notcontains $_ }
        Say ("seed files identical: " + (-not $seedKept) + "; other differences: " + ((Compare-Object $before $after | % { $_.InputObject.Split(' ')[0] } | Sort -Unique) -join ', '))
        Say ("Run value unchanged: " + ((Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -ErrorAction SilentlyContinue).Murmur -eq $runBefore) + " (" + [bool]$runBefore + ")")
        $log = Get-ChildItem $app -Recurse -Filter '*.log' -ErrorAction SilentlyContinue | Select -First 3
        foreach ($l in $log) { Say "log $($l.Name):"; Get-Content $l.FullName | Select-String -Pattern 'delta|full|apply|Applying|patch' | Select -Last 12 | % { Say ("  " + $_.Line) } }
        State
    }
    'uninstall' {
        Get-Process Murmur -ErrorAction SilentlyContinue | Stop-Process -Force
        & "$app\Update.exe" --uninstall --silent | Out-Null
        Start-Sleep 10; State
    }
    'status' { State }
}
