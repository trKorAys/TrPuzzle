@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0start-gpu-scan.ps1" %*
if errorlevel 1 (
  echo.
  echo TrPuzzle scan could not be started.
  pause
)
endlocal
