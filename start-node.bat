@echo off
chcp 65001 >nul
cd /d "%~dp0"
where node >nul 2>nul
if errorlevel 1 (
 echo Node.js не найден. Используй start.bat, ему Node не нужен.
 pause
 exit /b
)
node server.js
pause
