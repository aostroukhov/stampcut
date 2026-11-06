@echo off
rem Консольный режим StampCut из скриптов: дожидается завершения и возвращает код выхода.
"%~dp0StampCut.exe" %*
exit /b %ERRORLEVEL%
