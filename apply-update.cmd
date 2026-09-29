@echo off
rem One-click update: kills the running app, swaps in RobloxAuto.new.exe, starts it.
rem Double-click this, accept the one UAC prompt, done.
net session >nul 2>&1
if %errorlevel% neq 0 (
  powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b
)
echo Stopping RobloxAuto...
taskkill /F /IM RobloxAuto.exe >nul 2>&1
timeout /t 2 /nobreak >nul
echo Installing the new build...
copy /Y "%~dp0RobloxAuto.new.exe" "%~dp0RobloxAuto.exe" >nul
copy /Y "%~dp0RobloxAuto.exe" "%~dp0dist\RobloxAuto.exe" >nul 2>&1
del "%~dp0RobloxAuto.new.exe" >nul 2>&1
echo Starting...
start "" "%~dp0RobloxAuto.exe"
echo Update applied. This window closes in 4 seconds.
timeout /t 4 /nobreak >nul
