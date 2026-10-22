@echo off
chcp 65001 >nul
cd /d "%~dp0"
rem Analyze the newest demo in E:\demos. Read-only.
set "NODE=C:\Users\you\node-v24.21.0-win-x64\node.exe"
if not exist "%NODE%" set "NODE=node"

echo.
echo  RAUI FIELD REPORT - newest platform demo
echo  ----------------------------------
echo.

"%NODE%" field\field.mjs --latest-5e

echo.
echo  The report path is printed above. The assistant reads the files.
echo.
pause
