@echo off
rem One-command publish. Always the same output folder: app\RaUI.exe (no release-2/3/4 dirs).
rem Presets, score history and UI prefs live in %LOCALAPPDATA%\RawAccelUI, so a rebuild
rem never loses them.
rem NOTE: keep this file ASCII-only - cmd.exe reads it in the OEM codepage and UTF-8 Chinese
rem comments turn into garbage that it then tries to execute.
cd /d "%~dp0"

dotnet publish RaUI\RaUI.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:DebugType=none -p:EnableCompressionInSingleFile=true -o app
if errorlevel 1 (
  echo Publish failed. If app\RaUI.exe is running, close it first - the file is locked while the app runs.
  exit /b 1
)

rem RaProbe keeps its .pdb on purpose: when a diagnostic crashes, line numbers are the whole
rem difference between "we know where" and "ask the user to run it again and hope".
dotnet publish RaProbe\RaProbe.csproj -c Release -r win-x64 --self-contained false -o app
if errorlevel 1 (
  echo RaProbe publish failed.
  exit /b 1
)

rem ---- gate: the offline assertions, run from the binaries that just shipped ----
rem These never touch the driver, the display or the mouse, so publishing while a game is running
rem is safe. A build that fails them is not "probably fine".
echo.
echo Running the offline self-tests from the published binaries...
rem Write to a file and check the REAL exit code: piping straight into findstr would leave
rem findstr's exit code in errorlevel, and "it printed a line" is not "the tests passed".
app\RaProbe.exe --selftest > "%TEMP%\raui-selftest.txt" 2>&1
set "ST=%ERRORLEVEL%"
findstr /C:"checks," /C:"FAILED" "%TEMP%\raui-selftest.txt"
del "%TEMP%\raui-selftest.txt" >nul 2>&1
if not "%ST%"=="0" (
  echo   SELFTEST FAILED, exit code %ST% - do not use this build until it is green.
  exit /b 1
)

echo.
echo Done: app\RaUI.exe  +  app\RaProbe.exe
echo   offline checks:  app\RaProbe.exe --selftest
echo   which curve:     app\RaProbe.exe --curve-name
echo   tray check:      app\RaUI.exe --dump-tray
echo   icon check:      app\RaUI.exe --dump-ico RaUI\raui.ico
echo   ui render:       app\RaUI.exe --dump-ui (offscreen, no focus change)
echo   autostart check: app\RaUI.exe --autostart-check
echo   data dir: %LOCALAPPDATA%\RawAccelUI
