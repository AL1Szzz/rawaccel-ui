@echo off
rem Mouse packet jitter test - read only, nothing is written to the driver or to settings.json.
rem Keep this file ASCII-only: cmd.exe parses it in the OEM codepage, so non-ASCII text here can
rem be misread as commands (that already bit publish.cmd once). Chinese output from the probe
rem itself is fine - the .NET console writes Unicode directly.
cd /d "%~dp0"

echo.
echo  ==========================================================
echo   MOUSE PACKET JITTER TEST   (read-only, about 40 seconds)
echo  ==========================================================
echo.
echo   What it measures: how regularly your mouse packets arrive,
echo   and how much acceleration is lost when one arrives late.
echo.
echo   HOW TO USE IT
echo     1. press any key here
echo     2. the moment the counter starts, MOVE THE MOUSE
echo        back and forth like aiming - keep moving it
echo     3. wait for the result table, then close this window
echo.
echo   Do NOT click inside this window while it runs: that puts
echo   the console in select mode and PAUSES the program.
echo.
pause

echo.
app\RaProbe.exe --poll-jitter 20

echo.
echo  ----------------------------------------------------------
echo  A text copy of the result is saved here:
echo     %LOCALAPPDATA%\RawAccelUI\jitter
echo  ----------------------------------------------------------
pause
