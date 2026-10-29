@echo off
rem Builds a single RigUninstaller.exe into the "publish" folder (needs the .NET 8 Desktop Runtime to run).
cd /d "%~dp0"
dotnet publish src\RigUninstaller -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=none -o publish
if errorlevel 1 (
  echo Publish failed.
  exit /b 1
)
echo.
echo Done: %~dp0publish\RigUninstaller.exe
