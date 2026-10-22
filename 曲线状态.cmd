@echo off
rem Which curve is actually live in the driver right now? Read-only, no write.
rem It asks RaProbe to show the driver state (without --yes it only looks, never writes),
rem then names the match so there is no sha12 guessing in the middle of a test run.
cd /d "%~dp0"
echo.
echo  Current driver state:
echo.
app\RaProbe.exe --apply-settings curves\A-accel0.06.json
echo.
echo  06a7469472b7  =  A  (X accel 0.06, baseline)
echo  fabd31e1dc88  =  B  (X accel 0.12)
echo.
pause
