@echo off
rem Compare aim-map spray sessions burst by burst, so the wall sprays cannot
rem pollute the number. Read-only: it only reads reports already on disk.
rem ASCII-only file: cmd.exe parses this in the OEM codepage.
set "NODE=C:\Users\you\node-v24.21.0-win-x64\node.exe"
if not exist "%NODE%" set "NODE=node"
cd /d "%~dp0"
echo.
echo  Burst-level spray comparison, the two newest aim-map sessions:
echo.
"%NODE%" field\spray-bursts.mjs --last 3
pause
