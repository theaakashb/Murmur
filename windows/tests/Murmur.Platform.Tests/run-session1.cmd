@echo off
rem Runs the tests that need the logged-in desktop (SessionFact). Start it in session 1, for example over SSH with:
rem   powershell -NoProfile -ExecutionPolicy Bypass -File C:\src\runui.ps1 -Exe <full path to this file> -Wait 120
rem Output goes to session1-results.txt next to this file; read it back afterwards.
cd /d "%~dp0\..\.."
"%ProgramFiles%\dotnet\dotnet.exe" test tests\Murmur.Platform.Tests\Murmur.Platform.Tests.csproj > "%~dp0session1-results.txt" 2>&1
