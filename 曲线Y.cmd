@echo off
rem Switch the live driver curve to Y: ONLY Y.inputOffset 2.5 -> 0.75.
rem X stays at offset 3.0 / accel 0.06 / cap 1.97; Y accel 0.15 and cap 1.83 are unchanged.
rem Content stays ASCII-only: cmd.exe parses this in the OEM codepage.
rem The write is RaProbe --apply-settings: validate -> diff -> write -> read back ->
rem compare; it rolls the bytes back automatically if the read-back does not match.
cd /d "%~dp0"
echo.
echo  ============================================================
echo   Switching curve to  Y  =  Y inputOffset 0.75  (spray test)
echo  ============================================================
echo   Only Y.inputOffset changes. Everything else is identical to A.
echo   Your mouse may hitch for about a second while the driver takes the write.
echo   Do NOT press "write to driver" in the UI during the test - it would push the
echo   UI's own draft over whatever this script just set.
echo.
app\RaProbe.exe --apply-settings curves\Y-yoffset0.75.json --yes
echo.
echo  If the line above says the read-back matched, Y0.75 is live.
echo  Next: run the spray-Y capture again and compare the Y report with the 2.5 baseline.
pause
