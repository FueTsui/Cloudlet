@echo off
set "CLOUDLET_NATIVE=%~dp0release\Cloudlet-2.0.0-win-x64\Cloudlet.exe"
if not exist "%CLOUDLET_NATIVE%" (
  echo Please build the Windows 11 edition with scripts\build.ps1 first.
  pause
  exit /b 1
)
start "" "%CLOUDLET_NATIVE%"
