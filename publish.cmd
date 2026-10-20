@echo off
chcp 65001 >nul
rem 一键发布：永远输出到 app\RaUI.exe，不再新建 release-2/3/4 目录。
rem 日志、AI 方案审计、界面偏好都在 %LOCALAPPDATA%\RawAccelUI，换版本不会丢历史。
cd /d "%~dp0"
dotnet publish RaUI\RaUI.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:DebugType=none -p:EnableCompressionInSingleFile=true -o app
if errorlevel 1 (
  echo 发布失败：如果 app\RaUI.exe 正在运行，先关掉它再试。
  exit /b 1
)
echo.
echo 完成：app\RaUI.exe   数据目录：%LOCALAPPDATA%\RawAccelUI
