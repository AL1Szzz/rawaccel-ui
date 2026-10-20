@echo off
rem One-command publish. Always the same output folder: app\RaUI.exe (no release-2/3/4 dirs).
rem Logs, patch audits and UI prefs live in %LOCALAPPDATA%\RawAccelUI, so a rebuild never loses them.
rem NOTE: keep this file ASCII-only - cmd.exe reads it in the OEM codepage and UTF-8 Chinese
rem comments turn into garbage that it then tries to execute.
cd /d "%~dp0"

dotnet publish RaUI\RaUI.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:DebugType=none -p:EnableCompressionInSingleFile=true -o app
if errorlevel 1 (
  echo Publish failed. If app\RaUI.exe is running, close it first - the file is locked while the app runs.
  exit /b 1
)

dotnet publish RaProbe\RaProbe.csproj -c Release -r win-x64 --self-contained false -p:DebugType=none -o app
if errorlevel 1 (
  echo RaProbe publish failed.
  exit /b 1
)

echo.
echo Done: app\RaUI.exe  +  app\RaProbe.exe
echo   offline checks:  app\RaProbe.exe --selftest
echo   tray check:      app\RaUI.exe --dump-tray
echo   autostart check: app\RaUI.exe --autostart-check
echo   data dir: %LOCALAPPDATA%\RawAccelUI
