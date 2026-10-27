@echo off
chcp 65001 >nul
rem How many mouse packets does Windows actually deliver? Raw input, per device, read only.
rem It never moves the pointer, never writes the driver and never takes focus, so a game can
rem stay in the front window while this runs.
rem
rem Do the A/B: run this once with the mouse at 2000 Hz, once at 4000 Hz, same sweeping motion.
rem Compare the "hz=" numbers. Doubled  = the packets really arrive, so a browser page that
rem still says ~1200 is just the page. Unchanged = Windows merged them before any app could
rem count them, and no user-mode tool (web testers included) can show 4K.
cd /d "%~dp0"

set "OUT=%LOCALAPPDATA%\RawAccelUI\logs"
if not exist "%OUT%" mkdir "%OUT%"
set "STAMP=manual"
for /f "usebackq delims=" %%i in (`powershell -NoProfile -Command "Get-Date -Format yyyyMMdd_HHmmss"`) do set "STAMP=%%i"
set "LOG=%OUT%\hz-%STAMP%.txt"

echo.
echo  Move the mouse continuously for 15 seconds. Figure-8s or side to side, do not stop.
echo  Anything you have open stays open; this only listens.
echo.
app\RaProbe.exe --hz 15 > "%LOG%" 2>&1
type "%LOG%"
echo.
echo  saved: %LOG%
echo.
pause
