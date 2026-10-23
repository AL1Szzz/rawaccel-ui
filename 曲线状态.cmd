@echo off
chcp 65001 >nul
rem What curve is the driver actually running right now? Read-only, never writes.
rem The curve name and description come from RaProbe itself (UTF-8), so this file
rem stays ASCII-only and just switches the console codepage for the output.
rem Switching curves happens in the app: RaUI, first page, top row.
cd /d "%~dp0"
echo.
app\RaProbe.exe --curve-name
echo.
echo  --- raw numbers in the driver (read only) ---
app\RaProbe.exe > "%TEMP%\raui_state.txt" 2>&1
findstr /C:"accel_x:" /C:"accel_y:" /C:"output_dpi:" "%TEMP%\raui_state.txt"
del "%TEMP%\raui_state.txt" >nul 2>&1
echo.
echo  yx = the vertical multiplier. ud only acts on the UP direction, so a
echo  down-pull spray can only be boosted through yx.
echo.
pause
