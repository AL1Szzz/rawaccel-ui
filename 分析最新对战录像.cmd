@echo off
chcp 65001 >nul
cd /d "%~dp0"
rem Analyze the newest demo in E:\demos. Read-only.
set "NODE=node"
if exist "%USERPROFILE%\node-v24.21.0-win-x64\node.exe" set "NODE=%USERPROFILE%\node-v24.21.0-win-x64\node.exe"
rem RAUI_NODE wins when set, so a non-standard node install needs no script edits.
if defined RAUI_NODE set "NODE=%RAUI_NODE%"

echo.
echo  RAUI FIELD REPORT - newest platform demo
echo  ----------------------------------
echo.

"%NODE%" field\field.mjs --latest-5e

echo.
echo  The report path is printed above. The assistant reads the files.
echo.
pause
