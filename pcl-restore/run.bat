@echo off
setlocal enabledelayedexpansion
rem Launch PCL2 (if not running) and inject the download-restore payload.
rem
rem Usage:
rem   run.bat [path\to\Plain Craft Launcher 2.exe]
rem
rem Env overrides:
rem   OPENINJECTOR_TOOL  full path to OpenInjectorTool.exe
rem   PCL2_EXE           full path to PCL2

set "ROOT=%~dp0"
set "OUT=%ROOT%out"

if not "%~1"=="" set "PCL2_EXE=%~1"
if not defined PCL2_EXE set "PCL2_EXE=%ROOT%..\Plain Craft Launcher 2.exe"
for %%F in ("%PCL2_EXE%") do (
  set "PCL_NAME=%%~nxF"
  set "PCL_DIR=%%~dpF"
)
if not exist "%PCL2_EXE%" (
  echo [ERROR] PCL2 not found: "%PCL2_EXE%"
  echo         pass the path as the first argument or set PCL2_EXE
  exit /b 1
)
if not exist "%OUT%\bootstrap64.dll" (
  echo [ERROR] payload not built, run build.bat first
  exit /b 1
)

rem ---- pick bootstrap dll matching this OS ----
if defined ProgramW6432 (
  set "BOOTSTRAP=%OUT%\bootstrap64.dll"
  set "TOOL_NAME=OpenInjectorTool.exe"
) else (
  set "BOOTSTRAP=%OUT%\bootstrap32.dll"
  set "TOOL_NAME=OpenInjectorTool32.exe"
)

rem ---- locate OpenInjector ----
if not defined OPENINJECTOR_TOOL (
  set "CAND=%ROOT%..\..\OpenInjector\build\bin\!TOOL_NAME!"
  if exist "!CAND!" set "OPENINJECTOR_TOOL=!CAND!"
)
if not defined OPENINJECTOR_TOOL (
  for /f "delims=" %%T in ('where !TOOL_NAME! 2^>nul') do (
    if not defined OPENINJECTOR_TOOL set "OPENINJECTOR_TOOL=%%T"
  )
)
if not defined OPENINJECTOR_TOOL (
  echo [ERROR] !TOOL_NAME! not found
  echo         set OPENINJECTOR_TOOL to its full path
  exit /b 1
)

echo Injector : "%OPENINJECTOR_TOOL%"
echo Payload  : "%BOOTSTRAP%"
echo Target   : "%PCL2_EXE%"

rem ---- start PCL2 if it is not running ----
set "RUNNING="
for /f "tokens=2 delims=," %%P in ('tasklist /FI "IMAGENAME eq %PCL_NAME%" /FO CSV /NH 2^>nul') do set "RUNNING=1"

if not defined RUNNING (
  echo Starting PCL2 ...
  start "" /d "%PCL_DIR%" "%PCL2_EXE%"
  for /l %%I in (1,1,60) do (
    if not defined RUNNING (
      for /f "tokens=2 delims=," %%P in ('tasklist /FI "IMAGENAME eq %PCL_NAME%" /FO CSV /NH 2^>nul') do set "RUNNING=1"
      if not defined RUNNING ping -n 2 127.0.0.1 >nul
    )
  )
  if not defined RUNNING (
    echo [ERROR] PCL2 did not start
    exit /b 1
  )
  rem give the CLR a moment to finish initialising
  ping -n 7 127.0.0.1 >nul
) else (
  echo PCL2 already running
)

rem ---- inject into every matching process (PCL2 may spawn a helper) ----
set "COUNT=0"
for /f "tokens=2 delims=," %%P in ('tasklist /FI "IMAGENAME eq %PCL_NAME%" /FO CSV /NH 2^>nul') do (
  set "PID=%%~P"
  echo Injecting into pid !PID! ...
  "%OPENINJECTOR_TOOL%" --inject "%BOOTSTRAP%" --pid !PID!
  if not errorlevel 1 set /a COUNT+=1
)

if "%COUNT%"=="0" (
  echo [ERROR] injection failed
  exit /b 1
)
echo Done. Log: "%OUT%\PCLRestore.log"
endlocal
