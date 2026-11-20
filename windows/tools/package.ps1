<#
 Build a Velopack release of Murmur for Windows (PLAN.md "Updates and releases").
 Usage:  .\package.ps1 -Version 0.0.2 -Out C:\src\w-m6\releases [-Shortcuts None] [-NoPortable]
 Put the previous release's files in -Out first (vpk download github, or a copy) and vpk adds a delta package.
 Nothing here uploads anything.
#>
param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$Out,
    [string]$Shortcuts = 'Desktop,StartMenuRoot',
    [switch]$NoPortable
)
$ErrorActionPreference = 'Stop'
$VpkVersion = '1.2.161'   # must equal the Velopack PackageReference in Murmur.App.csproj
$repo = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$tools = Join-Path $repo 'windows\tools\.vpk'
$publish = Join-Path $Out "publish-$Version"

if (-not (Test-Path (Join-Path $tools 'vpk.exe'))) {
    dotnet tool install vpk --version $VpkVersion --tool-path $tools
    if ($LASTEXITCODE) { throw 'vpk install failed' }
}
if ((& (Join-Path $tools 'vpk.exe') -h 2>&1 | Out-String) -notmatch [regex]::Escape($VpkVersion)) { Write-Warning "vpk is not $VpkVersion" }

dotnet publish (Join-Path $repo 'windows\src\Murmur.App') -c Release -r win-x64 -p:Version=$Version -o $publish
if ($LASTEXITCODE) { throw 'publish failed' }

$vpkArgs = @('pack', '--packId', 'MurmurApp', '--packVersion', $Version, '--packDir', $publish,
    '--mainExe', 'Murmur.exe', '--packTitle', 'Murmur', '--outputDir', $Out, '--runtime', 'win-x64', '--shortcuts', $Shortcuts)
if ($NoPortable) { $vpkArgs += '--noPortable' }

# SIGNING (not enabled yet): append one of these to $vpkArgs once the user has a signing identity. Every signature must be timestamped.
#   Azure Artifact Signing:  '--azureTrustedSignFile', '<metadata.json>'
#   signtool + certificate:  '--signParams', '/fd sha256 /tr http://timestamp.digicert.com /td sha256 /a'
# (The signing options appear only in `vpk [win] pack -H` on Windows; check them against the pinned vpk before use.)

& (Join-Path $tools 'vpk.exe') @vpkArgs
if ($LASTEXITCODE) { throw 'vpk pack failed' }
Remove-Item $publish -Recurse -Force
