@echo off
chcp 65001 >nul
rem Same counter as the other one, but listening on a REAL FOREGROUND WINDOW - the path a game
rem in focus uses. A small window pops up: leave it alone, do not click another window, and keep
rem moving the mouse. This is the control for the background-sink number in hz-*-foreground.txt
rem vs hz-*-background.
cd /d "%~dp0"

set "OUT=%LOCALAPPDATA%\RawAccelUI\logs"
if not exist "%OUT%" mkdir "%OUT%"
set "STAMP=manual"
for /f "usebackq delims=" %%i in (`powershell -NoProfile -Command "Get-Date -Format yyyyMMdd_HHmmss"`) do set "STAMP=%%i"
set "LOG=%OUT%\hz-foreground-%STAMP%.txt"

echo.
echo  A small window will appear. Do NOT click away from it.
echo  Move the mouse continuously for 20 seconds. Figure-8s, side to side, do not stop.
echo  Log: %LOG%
echo.
app\RaProbe.exe --hz 20 --hz-fg > "%LOG%" 2>&1
type "%LOG%"
echo.
echo  saved: %LOG%
echo.
pause
