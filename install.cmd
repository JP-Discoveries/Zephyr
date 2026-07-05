@echo off
rem ============================================================
rem  Keep this window OPEN no matter what happens (errors, a bad
rem  label, a crash, or a double-click). We relaunch ourselves
rem  once under "cmd /k", which never auto-closes, so any error
rem  text stays on screen. Set _ZEPHYR_STAYOPEN=1 to skip this.
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

echo ============================================
echo    Zephyr - first-time setup
echo ============================================
echo.
echo This installs prerequisites (the .NET 10 SDK, if missing) and
echo builds Zephyr. You only need to run this once per PC.
echo.
echo A full log of this run is saved to:
echo    !LOG!
echo.

rem --- 0. If Zephyr is already running it locks its own files and the ---
rem ---    build will fail. Ask the user to close it first.            ---
tasklist /FI "IMAGENAME eq Zephyr.exe" 2>nul | "%SystemRoot%\System32\findstr.exe" /I /C:"Zephyr.exe" >nul
if not errorlevel 1 (
    echo Zephyr is currently RUNNING. That locks Zephyr.Core.dll and will make
    echo the build fail. Please close Zephyr completely, then
    echo press any key to continue . . .
    pause >nul
)

rem --- 1. Find a .NET 10 SDK (PATH, Program Files, or a user-local install) ---
call :ResolveDotnet
if !errorlevel! equ 0 goto :Build

echo The .NET 10 SDK is required to build Zephyr, but it was not found on this PC.
echo.
set "ANSWER="
set /p "ANSWER=Install the .NET 10 SDK now? [Y/N] "
if /i not "!ANSWER!"=="Y" (
    echo.
    echo Setup cancelled. Zephyr needs the .NET 10 SDK.
    echo Install it yourself from https://aka.ms/dotnet/download then re-run install.cmd.
    echo.
    pause
    exit /b 1
)

rem --- 2a. Preferred: winget (installs system-wide, may prompt for UAC) ---
where winget >nul 2>&1
if !errorlevel! equ 0 (
    echo.
    echo Installing the .NET 10 SDK via winget...
    winget install --id Microsoft.DotNet.SDK.10 -e --accept-source-agreements --accept-package-agreements
    rem winget updates the machine PATH; make the new dotnet visible to this session.
    set "PATH=%ProgramFiles%\dotnet;!PATH!"
    call :ResolveDotnet
    if !errorlevel! equ 0 goto :Build
)

rem --- 2b. Fallback: Microsoft's official script into the user profile (no admin) ---
echo.
echo Installing the .NET 10 SDK to your user profile (no admin required)...
powershell -NoProfile -ExecutionPolicy Bypass -Command "& { try { Invoke-WebRequest -UseBasicParsing 'https://dot.net/v1/dotnet-install.ps1' -OutFile \"$env:TEMP\dotnet-install.ps1\"; & \"$env:TEMP\dotnet-install.ps1\" -Channel 10.0 } catch { exit 1 } }"
set "PATH=%USERPROFILE%\.dotnet;!PATH!"
call :ResolveDotnet
if !errorlevel! equ 0 goto :Build

echo.
echo Automatic install did not complete. Please install the .NET 10 SDK manually:
echo   https://aka.ms/dotnet/download
echo Then re-run install.cmd.
echo.
pause
exit /b 1

:Build
echo Using .NET SDK: !DOTNET!
echo.
echo Building Zephyr (Release) - this may take a minute...
echo.
rem Capture the build to the log AND show it on screen.
"!DOTNET!" build "%PROJECT%" -c Release --nologo > "!LOG!" 2>&1
set "BUILDRC=!errorlevel!"
type "!LOG!"
if !BUILDRC! neq 0 (
    echo.
    echo *** Build FAILED with exit code !BUILDRC!.
    echo *** The full log above is also saved at: !LOG!
    echo *** If it mentions a file "locked by Zephyr", close Zephyr and run this again.
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

rem ============================================================
rem  :ResolveDotnet  - sets DOTNET to a dotnet.exe that has a
rem  10.x SDK; returns errorlevel 0 if found, 1 otherwise.
rem ============================================================
:ResolveDotnet
for %%D in ("dotnet" "%ProgramFiles%\dotnet\dotnet.exe" "%USERPROFILE%\.dotnet\dotnet.exe") do (
    "%%~D" --list-sdks 2>nul | findstr /b "10." >nul 2>&1 && ( set "DOTNET=%%~D" & exit /b 0 )
)
exit /b 1
