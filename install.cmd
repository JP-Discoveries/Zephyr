@echo off
rem ============================================================
rem  Keep this window OPEN no matter what (errors, crash, or a
rem  double-click). Relaunch once under "cmd /k", which never
rem  auto-closes. This script is intentionally LABEL-FREE (no
rem  goto / call :label) so LF vs CRLF line endings can't break
rem  it - cmd.exe misparses label lookups in LF-only batch files.
rem ============================================================
if not defined _ZEPHYR_STAYOPEN (
    set "_ZEPHYR_STAYOPEN=1"
    cmd /k "%~f0"
    exit /b
)

setlocal enabledelayedexpansion
cd /d "%~dp0"

set "PROJECT=Zephyr.UI\Zephyr.UI.csproj"
set "RELEASEDIR=Zephyr.UI\bin\Release"
set "LOG=%~dp0install-log.txt"
set "FINDSTR=%SystemRoot%\System32\findstr.exe"

echo ============================================
echo    Zephyr - first-time setup
echo ============================================
echo.
echo This installs prerequisites (the .NET 10 SDK, if missing) and
echo builds Zephyr. You only need to run this once per PC.
echo.

rem --- Zephyr running? It locks its own DLLs and the build fails. ---
tasklist /FI "IMAGENAME eq Zephyr.exe" 2>nul | "%FINDSTR%" /I /C:"Zephyr.exe" >nul
if not errorlevel 1 (
    echo Zephyr is currently RUNNING, which locks its files and will make the
    echo build fail. Please close Zephyr completely, then
    echo press any key to continue . . .
    pause >nul
)

rem --- Detect a .NET 10 SDK (PATH, Program Files, or user-local) ---
set "DOTNET="
for %%D in ("dotnet" "%ProgramFiles%\dotnet\dotnet.exe" "%USERPROFILE%\.dotnet\dotnet.exe") do (
    if not defined DOTNET (
        "%%~D" --list-sdks 2>nul | "%FINDSTR%" /b "10." >nul 2>&1
        if not errorlevel 1 set "DOTNET=%%~D"
    )
)

rem --- If missing, ask permission to install it ---
set "DOINSTALL="
if not defined DOTNET (
    echo The .NET 10 SDK was not found. It is required to build Zephyr.
    echo.
    set "ANSWER="
    set /p "ANSWER=Install the .NET 10 SDK now? [Y/N] "
    if /i "!ANSWER!"=="Y" set "DOINSTALL=1"
)

if not defined DOTNET if not defined DOINSTALL (
    echo.
    echo Setup cancelled. Install the .NET 10 SDK yourself from
    echo   https://aka.ms/dotnet/download
    echo then re-run install.cmd.
    echo.
    pause
    exit /b 1
)

rem --- Try winget first (system-wide, may prompt for UAC) ---
if defined DOINSTALL if not defined DOTNET (
    where winget >nul 2>&1
    if not errorlevel 1 (
        echo.
        echo Installing the .NET 10 SDK via winget...
        winget install --id Microsoft.DotNet.SDK.10 -e --accept-source-agreements --accept-package-agreements
        set "PATH=%ProgramFiles%\dotnet;!PATH!"
    )
)

rem --- Re-detect after winget ---
for %%D in ("dotnet" "%ProgramFiles%\dotnet\dotnet.exe" "%USERPROFILE%\.dotnet\dotnet.exe") do (
    if not defined DOTNET (
        "%%~D" --list-sdks 2>nul | "%FINDSTR%" /b "10." >nul 2>&1
        if not errorlevel 1 set "DOTNET=%%~D"
    )
)

rem --- Fallback: Microsoft's official user-local installer (no admin) ---
if defined DOINSTALL if not defined DOTNET (
    echo.
    echo Installing the .NET 10 SDK to your user profile (no admin required)...
    powershell -NoProfile -ExecutionPolicy Bypass -Command "& { try { Invoke-WebRequest -UseBasicParsing 'https://dot.net/v1/dotnet-install.ps1' -OutFile \"$env:TEMP\dotnet-install.ps1\"; & \"$env:TEMP\dotnet-install.ps1\" -Channel 10.0 } catch { exit 1 } }"
    set "PATH=%USERPROFILE%\.dotnet;!PATH!"
)

rem --- Re-detect after the fallback ---
for %%D in ("dotnet" "%ProgramFiles%\dotnet\dotnet.exe" "%USERPROFILE%\.dotnet\dotnet.exe") do (
    if not defined DOTNET (
        "%%~D" --list-sdks 2>nul | "%FINDSTR%" /b "10." >nul 2>&1
        if not errorlevel 1 set "DOTNET=%%~D"
    )
)

if not defined DOTNET (
    echo.
    echo The .NET 10 SDK is still not available. Please install it manually:
    echo   https://aka.ms/dotnet/download
    echo then re-run install.cmd.
    echo.
    pause
    exit /b 1
)

rem --- Build (capture to the log AND show it on screen) ---
echo.
echo Using .NET SDK: !DOTNET!
echo.
echo Building Zephyr (Release) - this may take a minute...
echo.
"!DOTNET!" build "%PROJECT%" -c Release --nologo > "!LOG!" 2>&1
set "BUILDRC=!errorlevel!"
type "!LOG!"
if not "!BUILDRC!"=="0" (
    echo.
    echo *** Build FAILED with exit code !BUILDRC!.
    echo *** The full log above is also saved at: !LOG!
    echo *** If it mentions a file "locked by Zephyr", close Zephyr and run again.
    echo.
    pause
    exit /b 1
)

set "EXE="
for /r "%RELEASEDIR%" %%F in (Zephyr.exe) do if not defined EXE if exist "%%F" set "EXE=%%F"
if not defined EXE (
    echo.
    echo Build reported success but Zephyr.exe was not found under %RELEASEDIR%.
    echo.
    pause
    exit /b 1
)

echo.
echo ============================================
echo    Setup complete.
echo.
echo    Launch Zephyr with:
echo      run.vbs   - no console window
echo      run.cmd   - console window
echo      Win+E     - once registered from Settings
echo ============================================
echo.
pause
exit /b 0
