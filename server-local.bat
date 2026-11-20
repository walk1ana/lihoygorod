@echo off
chcp 65001 >nul
cd /d "%~dp0"
if not exist RomchikServer.exe call build.bat
if not exist RomchikServer.exe exit /b 1
RomchikServer.exe
pause
