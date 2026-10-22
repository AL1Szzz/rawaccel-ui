@echo off
rem Switch the live driver curve to B: X acceleration 0.12. Identical to A in every other
rem field (diff is one line). Content stays ASCII-only: cmd.exe parses this in the OEM codepage.
cd /d "%~dp0"
echo.
echo  ============================================================
echo   Switching curve to  B  =  X acceleration 0.12
echo  ============================================================
echo   Only X.acceleration differs from A. Everything else is byte-identical.
echo   Your mouse may hitch for about a second while the driver takes the write.
echo   Do NOT press "write to driver" in the UI during the test - it would push the
echo   UI's own draft over whatever these scripts just set.
echo.
app\RaProbe.exe --apply-settings curves\B-accel0.12.json --yes
echo.
echo  If the line above says the read-back matched, B is live.
pause
