@echo off
setlocal enabledelayedexpansion
rem Self-test: start PCL2 with autotest flag, inject, dump payload log, then kill PCL2.
rem Usage: test-run.bat [path\to\Plain Craft Launcher 2.exe]

set "ROOT=%~dp0"
set "OUT=%ROOT%out"

set "PCL2_EXE=%~1"
if not defined PCL2_EXE set "PCL2_EXE=%ROOT%..\Plain Craft Launcher 2.exe"
for %%F in ("%PCL2_EXE%") do set "PCL_NAME=%%~nxF"

del /q "%OUT%\PCLRestore.log" 2>nul
del /q "%OUT%\bootstrap.log" 2>nul

set "PCLRESTORE_AUTOTEST=1"
call "%ROOT%run.bat" %*
if errorlevel 1 exit /b 1

ping -n 36 127.0.0.1 >nul

echo.
echo --- PCLRestore.log ---
if exist "%OUT%\PCLRestore.log" goto :showpcl
echo (missing)
goto :bootlog
:showpcl
type "%OUT%\PCLRestore.log"
:bootlog

echo.
echo --- bootstrap.log ---
if exist "%OUT%\bootstrap.log" type "%OUT%\bootstrap.log"
if not exist "%OUT%\bootstrap.log" echo (missing)

taskkill /F /IM "%PCL_NAME%" >nul 2>&1
endlocal
