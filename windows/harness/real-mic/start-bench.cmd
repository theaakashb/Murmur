@echo off
rem Starts Murmur for the real-speech comparison (windows/INSTALL-TEST.md, part B): a separate data folder, so your
rem own History and settings stay untouched, and each recording kept for the engine comparison (spec/test-hooks.md).
set MURMUR_DATA_DIR=%LOCALAPPDATA%\Murmur-bench
set MURMUR_KEEP_RECORDINGS=1
rem Optional argument: the full path of Murmur.exe (default: %LOCALAPPDATA%\Programs\Murmur-dev\Murmur.exe).
set "EXE=%~1"
if "%EXE%"=="" set "EXE=%LOCALAPPDATA%\Programs\Murmur-dev\Murmur.exe"
start "" "%EXE%"
