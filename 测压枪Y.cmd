@echo off
chcp 65001 >nul
cd /d "%~dp0"
rem Capture 60 s of raw mouse packets during a real spray, then analyze the Y
rem component. Read-only; no driver writes. ASCII-only: cmd OEM codepage.
set "NODE=node"
if exist "%USERPROFILE%\node-v24.21.0-win-x64\node.exe" set "NODE=%USERPROFILE%\node-v24.21.0-win-x64\node.exe"
rem RAUI_NODE wins when set, so a non-standard node install needs no script edits.
if defined RAUI_NODE set "NODE=%RAUI_NODE%"

echo.
echo  RAUI SPRAY-Y CAPTURE   (read-only, 60 seconds)
echo  ============================================
echo  What it measures: how fast your hand actually pulls down while you spray,
echo  compared with the Y input threshold that is live in the driver right now.
echo  The report reads the driver itself, so it knows both the Y threshold and
echo  the vertical multiplier (curve V sets that to 1.25). Hand speed = the counts
echo  you see divided by that multiplier, because the multiplier is applied AFTER
echo  the curve - a multiplier can never make the curve arm earlier.
echo.
echo  Note: this rig measures the Y CURVE question. A vertical multiplier is not
echo  judged here (the tool divides it out by definition); judge it in the aim map.
echo.
echo  HOW TO DO IT
echo    1. Load Aim Botz or any map with a wall, pick AK-47.
echo    2. Stand 5-10 m from the wall.
echo    3. Press any key here, then WIGGLE THE MOUSE IN THIS WINDOW until
echo       the counter starts (it needs 200 px of travel). Then immediately
echo       Alt-Tab back to CS2 and hold a full spray at the wall, pulling
echo       the mouse down to control recoil, for the whole 60 seconds.
echo       Fire 3-4 full AK mags; reload and continue. Pauses are counted
echo       in the file but excluded from the speed rates.
echo    4. Do NOT click inside this window while it runs: quick-edit mode
echo       pauses the program.
echo.
echo  The probe's own text says "move back and forth"; ignore that part.
echo  It only needs the mouse to move. Your spray is the measurement.
echo.
pause

app\RaProbe.exe --poll-jitter 60

echo.
echo  ---- Y analysis ----
"%NODE%" field\spray-y.mjs --latest

echo.
echo  The report path is printed above. The assistant reads the files.
echo.
pause
