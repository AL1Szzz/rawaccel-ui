@echo off
chcp 65001 >nul
rem How many mouse packets does Windows actually deliver? Raw input, per device, read only.
rem It never moves the pointer and never writes the driver.
rem
rem It measures BOTH delivery paths back to back, because they are NOT the same thing:
rem   1) background sink  = message-only window + RIDEV_INPUTSINK (no focus needed)
rem   2) foreground window = a real visible window, the path a game in focus uses
rem Windows coalesces the two differently, so one number alone means nothing. A small window
rem pops up for part 2: do not click away from it.
cd /d "%~dp0"

set "OUT=%LOCALAPPDATA%\RawAccelUI\logs"
if not exist "%OUT%" mkdir "%OUT%"
set "STAMP=manual"
for /f "usebackq delims=" %%i in (`powershell -NoProfile -Command "Get-Date -Format yyyyMMdd_HHmmss"`) do set "STAMP=%%i"
set "LOG=%OUT%\hz-%STAMP%.txt"

echo.
echo  PART 1 of 2, 15 seconds - background sink. Just keep moving the mouse.
echo.
app\RaProbe.exe --hz 15 > "%LOG%" 2>&1
echo  part 1 done.
echo.
echo  PART 2 of 2, 15 seconds - a window will pop up. Do NOT click away from it.
echo  Keep moving the mouse until it closes by itself.
echo.
app\RaProbe.exe --hz 15 --hz-fg >> "%LOG%" 2>&1
type "%LOG%"
echo.
echo  Compare the two active_hz_p50 numbers. Saved: %LOG%
echo.
pause
