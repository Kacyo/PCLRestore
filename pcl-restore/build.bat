@echo off
setlocal enabledelayedexpansion
rem Build bootstrap DLLs + managed payload.
rem Override compilers via env vars: CSC=... GCC=...

set "ROOT=%~dp0"
set "OUT=%ROOT%out"
if not exist "%OUT%" mkdir "%OUT%"

rem ---- locate csc.exe (any .NET Framework 4.x) ----
if not defined CSC (
  for %%V in (Framework64 Framework) do (
    if not defined CSC if exist "%WINDIR%\Microsoft.NET\%%V\v4.0.30319\csc.exe" (
      set "CSC=%WINDIR%\Microsoft.NET\%%V\v4.0.30319\csc.exe"
    )
  )
)
if not defined CSC (
  echo [ERROR] csc.exe not found, set CSC=full path to csc.exe
  exit /b 1
)

rem ---- locate gcc (32/64-bit mingw) ----
if not defined GCC (
  for /f "delims=" %%G in ('where gcc 2^>nul') do if not defined GCC set "GCC=%%G"
)
if not defined GCC (
  echo [ERROR] gcc not found in PATH, set GCC=full path to gcc.exe
  exit /b 1
)

echo [1/3] building bootstrap DLLs
"%GCC%" -m64 -shared -O2 -s -o "%OUT%\bootstrap64.dll" "%ROOT%native\bootstrap.c"
if errorlevel 1 exit /b 1
"%GCC%" -m32 -shared -O2 -s -o "%OUT%\bootstrap32.dll" "%ROOT%native\bootstrap.c"
if errorlevel 1 exit /b 1

echo [2/3] building managed payload
"%CSC%" /nologo /target:library /platform:anycpu ^
  "/out:%OUT%\PCLRestore.Payload.dll" ^
  "/r:%ROOT%tools\0Harmony.dll" ^
  "/resource:%ROOT%tools\0Harmony.dll,0Harmony.dll" ^
  /r:System.dll /r:System.Core.dll /r:System.IO.Compression.dll ^
  "%ROOT%payload\Payload.cs"
if errorlevel 1 exit /b 1
copy /y "%ROOT%tools\0Harmony.dll" "%OUT%\0Harmony.dll" >nul

echo [3/3] done
dir /b "%OUT%"
endlocal
