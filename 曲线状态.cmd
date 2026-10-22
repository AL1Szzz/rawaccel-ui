@echo off
rem Which curve is actually live in the driver right now? Read-only, no write.
rem Two questions it answers: the sha (which .cmd last wrote) and the numbers the
rem driver really holds - including the vertical output multiplier, which the
rem sha line does not print.
rem Content stays ASCII-only: cmd.exe parses this file in the OEM codepage.
cd /d "%~dp0"
echo.
echo  Live driver numbers (read-only dump):
echo.
app\RaProbe.exe > "%TEMP%\raui_state.txt" 2>&1
findstr /C:"accel_x:" /C:"accel_y:" /C:"output_dpi:" "%TEMP%\raui_state.txt"
del "%TEMP%\raui_state.txt" >nul 2>&1
echo.
echo  (yx is the vertical multiplier; ud only ever acts on y less than 0, which is
echo   the UP direction, so a down-pull spray can only be boosted through yx.)
echo.
echo  Curve identity check (compares the driver with curve A, does not write):
echo.
app\RaProbe.exe --apply-settings curves\A-accel0.06.json
echo.
echo  06a7469472b7  =  A  (X accel 0.06, baseline, yx 1.0)
echo  fabd31e1dc88  =  B  (X accel 0.12)
echo  799a7ca8e749  =  Y  (Y inputOffset 0.75, spray experiment)
echo  c63b44cfe7a0  =  V  (yx 1.25, vertical multiplier)
echo.
echo  A "change: yxRatio" or "change: Y.inputOffset" line above means the driver is
echo  NOT on A right now, whatever the sha says about A.
echo.
pause
