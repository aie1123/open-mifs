@echo off
rem OpenMIFS launcher (CLI) - elevate + bypass execution policy
setlocal
set "PS1=%~dp0src\mifs.ps1"
set "ARGS=%*"
if not exist "%PS1%" goto :nofile
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "$a=@('-NoProfile','-ExecutionPolicy','Bypass','-NoExit','-File','%PS1%'); if('%ARGS%' -ne ''){ $a += '%ARGS%'.Split(' ') }; Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList $a"
if errorlevel 1 goto :failed
goto :eof
:nofile
echo [ERROR] not found: %PS1%
pause
goto :eof
:failed
echo [ERROR] elevation cancelled. Manual steps:
echo   1) Right-click PowerShell -^> Run as administrator
echo   2) Set-ExecutionPolicy -Scope Process Bypass -Force
echo   3) cd /d "%~dp0"
echo   4) .\src\mifs.ps1 status
pause
