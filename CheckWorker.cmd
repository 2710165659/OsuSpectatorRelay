@echo off
chcp 65001 >nul
echo Checking Worker authentication, HTTPS, WebSocket and preferred IPs...
start "" /wait "%~dp0OsuSpectatorRelay.exe" check
if errorlevel 1 (echo Check failed. See runtime\relay.log.) else (echo Check passed. Preferred IP saved.)
pause
