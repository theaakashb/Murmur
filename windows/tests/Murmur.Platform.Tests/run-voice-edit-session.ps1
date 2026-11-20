# After building the Release app, Platform tests and FakeProvider, run in the console session through runps.ps1.
$ErrorActionPreference = 'Stop'
$windows = (Get-Item $PSScriptRoot).Parent.Parent.FullName
$env:Path = ($env:Path, [Environment]::GetEnvironmentVariable('Path', 'Machine'), [Environment]::GetEnvironmentVariable('Path', 'User')) -join ';'
$env:MURMUR_VOICE_EDIT_TEST_APP = "$windows\src\Murmur.App\bin\Release\net10.0-windows10.0.22621.0\win-x64\Murmur.exe"
$env:MURMUR_VOICE_EDIT_TEST_PROVIDER = "$windows\harness\FakeProvider\bin\Release\net10.0\FakeProvider.dll"
$env:MURMUR_VOICE_EDIT_TEST_ROOT = "$(Split-Path $windows -Parent)\voice-edit-test-runs"
$null = New-Item -ItemType Directory -Force $env:MURMUR_VOICE_EDIT_TEST_ROOT
$project = "$PSScriptRoot\Murmur.Platform.Tests.csproj"
$log = "$env:MURMUR_VOICE_EDIT_TEST_ROOT\session-test.log"
cmd /d /c "dotnet test `"$project`" -c Release --no-build --filter FullyQualifiedName~VoiceEditDesktopTests > `"$log`" 2>&1"
exit $LASTEXITCODE
