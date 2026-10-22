@echo off
rem Switch the live driver curve to A: X acceleration 0.06 (the long-used baseline).
rem Content stays ASCII-only: cmd.exe parses this file in the OEM codepage.
rem The write itself is RaProbe --apply-settings: validate -> diff -> write -> read back ->
rem compare, and it rolls the bytes back automatically if the read-back does not match.
cd /d "%~dp0"
echo.
echo  ============================================================
echo   Switching curve to  A  =  X acceleration 0.06  (baseline)
echo  ============================================================
echo   Your mouse may hitch for about a second while the driver takes the write.
echo   Do NOT press "write to driver" in the UI during the test - it would push the
echo   UI's own draft over whatever these scripts just set.
echo.
app\RaProbe.exe --apply-settings curves\A-accel0.06.json --yes
echo.
echo  If the line above says the read-back matched, A is live.
pause
