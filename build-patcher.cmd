@echo off
REM Builds the standalone remote-launcher patcher and publishes it into the mod's wwwroot.
REM Run this before "dotnet build -c Release" whenever the patcher or shared payload code changed.
setlocal
cd /d %~dp0

dotnet publish patcher\CLB.LauncherPatcher.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o wwwroot\LauncherPatcher\win
if errorlevel 1 exit /b 1

dotnet publish patcher\CLB.LauncherPatcher.csproj -c Release -r linux-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o wwwroot\LauncherPatcher\linux
if errorlevel 1 exit /b 1

echo.
echo Patcher published to wwwroot\LauncherPatcher (win + linux). Now run: dotnet build -c Release
