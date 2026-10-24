@echo off
rem Compare aim-map spray sessions burst by burst, so the wall sprays cannot
rem pollute the number. Read-only: it only reads reports already on disk.
rem ASCII-only file: cmd.exe parses this in the OEM codepage.
set "NODE=node"
if exist "%USERPROFILE%\node-v24.21.0-win-x64\node.exe" set "NODE=%USERPROFILE%\node-v24.21.0-win-x64\node.exe"
rem RAUI_NODE wins when set, so a non-standard node install needs no script edits.
if defined RAUI_NODE set "NODE=%RAUI_NODE%"
cd /d "%~dp0"
echo.
echo  Burst-level spray comparison, the two newest aim-map sessions:
echo.
"%NODE%" field\spray-bursts.mjs --last 3
pause
