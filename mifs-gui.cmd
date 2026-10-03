@echo off
rem OpenMIFS launcher (GUI) - bypass policy; the ps1 self-elevates via UAC
setlocal
set "PS1=%~dp0src\mifs-gui.ps1"
if not exist "%PS1%" goto :nofile
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%PS1%"
goto :eof
:nofile
echo [ERROR] not found: %PS1%
pause
