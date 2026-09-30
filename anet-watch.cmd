@echo off
rem ANet build watcher: Claude writes build.request ("test" or "full") into this folder,
rem the watcher runs anet-build.cmd with that mode and deletes the request.
rem Start once, leave the window open (minimized). Ctrl+C to stop.
setlocal EnableExtensions
cd /d "%~dp0"
title ANet build watcher
echo ANet watcher bezi - ceka na build.request od Clauda. Okno nech otevrene (muzes minimalizovat).
:loop
if exist build.request (
  set /p REQ=<build.request
  del build.request
  call :run
)
timeout /t 5 /nobreak >nul
goto loop
:run
if /i not "%REQ%"=="full" set REQ=test
echo %date% %time% - build %REQ%...
call anet-build.cmd %REQ% nopause
echo %date% %time% - dokonceno, cekam dal.
exit /b 0
