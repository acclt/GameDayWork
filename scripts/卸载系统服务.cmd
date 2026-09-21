@echo off
setlocal
set "SERVICE_EXE=%~dp0GameDayWork.Service.exe"
if not exist "%SERVICE_EXE%" (
  echo [GameDayWork] 找不到与便携包配套的 GameDayWork.Service.exe。
  pause
  exit /b 2
)
net session >nul 2>&1
if "%errorlevel%"=="0" goto elevated
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "$p = Start-Process -FilePath '%SERVICE_EXE%' -ArgumentList 'uninstall' -Verb RunAs -Wait -PassThru; exit $p.ExitCode"
exit /b %errorlevel%
:elevated
"%SERVICE_EXE%" uninstall
set "RESULT=%errorlevel%"
if "%RESULT%"=="0" (
  echo [GameDayWork] 系统服务已卸载，保存的锁屏息屏时间已恢复。
) else (
  echo [GameDayWork] 卸载未完全成功，退出码 %RESULT%。请查看 %%ProgramData%%\GameDayWork\service-management.log。
)
pause
exit /b %RESULT%
