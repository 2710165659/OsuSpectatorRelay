@echo off
chcp 65001 >nul
echo Reading config.json and creating an osu shortcut beside the EXE...
start "" /wait "%~dp0OsuSpectatorRelay.exe" shortcut
if errorlevel 1 (echo Failed. Please check osuPath in config.json.) else (echo Shortcut created beside the EXE.)
pause
