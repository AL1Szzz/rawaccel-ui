@echo off
chcp 65001 >nul
cd /d "%~dp0"
rem Read the newest CS2 demo and write a field report. Drop a .dem on this
rem file to analyze that one instead. ASCII-only: cmd parses this in OEM codepage.
set "NODE=node"
if exist "%USERPROFILE%\node-v24.21.0-win-x64\node.exe" set "NODE=%USERPROFILE%\node-v24.21.0-win-x64\node.exe"
rem RAUI_NODE wins when set, so a non-standard node install needs no script edits.
if defined RAUI_NODE set "NODE=%RAUI_NODE%"

echo.
echo  RAUI FIELD REPORT
echo  -----------------
echo  Newest demo in the CS2 replays folder, or the .dem you dropped here.
echo.

if "%~1"=="" (
  "%NODE%" field\field.mjs --latest
) else (
  "%NODE%" field\field.mjs --demo "%~1"
)

echo.
echo  The report path is printed above. The assistant reads the files.
echo.
pause
