@echo off
setlocal
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0DeepSeekHarness.ps1" %*
if errorlevel 1 pause
endlocal
