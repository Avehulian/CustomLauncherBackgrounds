@echo off
REM Builds the in-game prompt client plugin and copies it into the mod's wwwroot so the mod
REM package can ship it (BepInEx\plugins\...) and remote players can download it from the SIC.
setlocal
cd /d %~dp0

dotnet build client-plugin\CustomLauncherBackgrounds.Gui.csproj -c Release
if errorlevel 1 exit /b 1

if not exist wwwroot\InGamePrompt mkdir wwwroot\InGamePrompt
copy /y client-plugin\bin\Release\CustomLauncherBackgrounds.Gui.dll wwwroot\InGamePrompt\ >nul

echo.
echo Client plugin copied to wwwroot\InGamePrompt. Now run: dotnet build -c Release
