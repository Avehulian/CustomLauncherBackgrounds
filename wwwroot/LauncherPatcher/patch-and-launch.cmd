@echo off
setlocal
echo Custom Launcher Backgrounds - update background from your SPT server
echo.
"%~dp0win\CustomLauncherBackgrounds.Patcher.exe" --path "%~dp0.." %*
if errorlevel 1 (
    echo.
    echo Background update failed - starting the launcher anyway.
    pause
)
start "" "%~dp0..\SPT.Launcher.exe"
endlocal
