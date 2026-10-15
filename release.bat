@echo off
rem Release helper. Examples:
rem   release.bat stable 6.3.3.0
rem   release.bat testing 6.3.3.1
rem   release.bat stable 6.3.3.0 -DryRun
rem   release.bat sync-config
if "%~1"=="" (
  echo usage: release.bat ^<stable^|testing^> ^<a.b.c.d^> [-DryRun]
  echo        release.bat sync-config
  exit /b 1
)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\release.ps1" %*
exit /b %ERRORLEVEL%
