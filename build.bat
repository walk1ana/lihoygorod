@echo off
chcp 65001 >nul
cd /d "%~dp0"
set "CSC=%windir%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%windir%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" goto nocsc
"%CSC%" /nologo /optimize+ /target:exe /out:RomchikServer.exe Server.cs
if errorlevel 1 goto fail
echo Готово: RomchikServer.exe
goto end
:nocsc
echo Не найден csc.exe из .NET Framework 4. Он есть в Windows 10 и 11 по умолчанию.
goto fail
:fail
pause
exit /b 1
:end
