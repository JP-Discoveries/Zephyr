@echo off
rem ============================================================
rem  Zephyr first-time setup.
rem  - Stays OPEN no matter what (relaunch once under cmd /k).
rem  - Writes install-log.txt from the very first step, so even
rem    an early exit leaves a log whose last line shows where it
rem    stopped.
rem  - Label-free and paren-safe: cmd.exe misparses LF batch
rem    files and parentheses inside ( ) blocks, so we avoid both.
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

rem Create the log immediately so it always exists.
> "!LOG!" echo Zephyr install log
>> "!LOG!" echo script = %~f0
>> "!LOG!" echo folder = %CD%
>> "!LOG!" echo [step] start

echo ============================================
echo    Zephyr - first-time setup
echo ============================================
echo.
echo This installs prerequisites - the .NET 10 SDK, if missing -
echo and builds Zephyr. You only need to run this once per PC.
echo.
>> "!LOG!" echo [step] intro shown

rem --- If Zephyr is running it locks its own DLLs and the build fails. ---
>> "!LOG!" echo [step] checking for a running Zephyr
tasklist /FI "IMAGENAME eq Zephyr.exe" 2>nul | "%FINDSTR%" /I /C:"Zephyr.exe" >nul
if not errorlevel 1 (
    echo Zephyr is currently RUNNING, which locks its files and will make the
    echo build fail. Please close Zephyr completely, then
    echo press any key to continue . . .
    pause >nul
)

rem --- Detect a .NET 10 SDK. Prefer dotnet on PATH, then known folders. ---
>> "!LOG!" echo [step] detecting .NET 10 SDK
set "DOTNET="
where dotnet >nul 2>&1
if not errorlevel 1 (
    dotnet --list-sdks 2>nul | "%FINDSTR%" /b "10." >nul 2>&1
    if not errorlevel 1 set "DOTNET=dotnet"
)
if not defined DOTNET if exist "!ProgramFiles!\dotnet\dotnet.exe" (
    "!ProgramFiles!\dotnet\dotnet.exe" --list-sdks 2>nul | "%FINDSTR%" /b "10." >nul 2>&1
    if not errorlevel 1 set "DOTNET=!ProgramFiles!\dotnet\dotnet.exe"
)
if not defined DOTNET if exist "!USERPROFILE!\.dotnet\dotnet.exe" (
    "!USERPROFILE!\.dotnet\dotnet.exe" --list-sdks 2>nul | "%FINDSTR%" /b "10." >nul 2>&1
    if not errorlevel 1 set "DOTNET=!USERPROFILE!\.dotnet\dotnet.exe"
)
>> "!LOG!" echo [step] detection done DOTNET=[!DOTNET!]

rem --- If missing, ask permission to install it. ---
set "DOINSTALL="
if not defined DOTNET (
    echo The .NET 10 SDK was not found. It is required to build Zephyr.
    echo.
    set "ANSWER="
    set /p "ANSWER=Install the .NET 10 SDK now? [Y/N] "
    >> "!LOG!" echo [step] user answered [!ANSWER!]
    if /i "!ANSWER!"=="Y" set "DOINSTALL=1"
)

if not defined DOTNET if not defined DOINSTALL (
    >> "!LOG!" echo [step] cancelled by user
    echo.
    echo Setup cancelled. Install the .NET 10 SDK yourself from
    echo   https://aka.ms/dotnet/download
    echo then re-run install.cmd.
    echo.
    pause
    exit /b 1
)

rem --- Try winget first. ---
if defined DOINSTALL if not defined DOTNET (
    >> "!LOG!" echo [step] trying winget
    where winget >nul 2>&1
    if not errorlevel 1 (
        echo.
        echo Installing the .NET 10 SDK via winget...
        winget install --id Microsoft.DotNet.SDK.10 -e --accept-source-agreements --accept-package-agreements
        set "PATH=!ProgramFiles!\dotnet;!PATH!"
    )
)

rem --- Re-detect after winget. ---
if not defined DOTNET (
    where dotnet >nul 2>&1
    if not errorlevel 1 (
        dotnet --list-sdks 2>nul | "%FINDSTR%" /b "10." >nul 2>&1
        if not errorlevel 1 set "DOTNET=dotnet"
    )
)

rem --- Fallback: Microsoft's official user-local installer, no admin. ---
if defined DOINSTALL if not defined DOTNET (
    >> "!LOG!" echo [step] trying dotnet-install.ps1
    echo.
    echo Installing the .NET 10 SDK to your user profile - no admin needed...
    powershell -NoProfile -ExecutionPolicy Bypass -Command "& { try { Invoke-WebRequest -UseBasicParsing 'https://dot.net/v1/dotnet-install.ps1' -OutFile \"$env:TEMP\dotnet-install.ps1\"; & \"$env:TEMP\dotnet-install.ps1\" -Channel 10.0 } catch { exit 1 } }"
    set "PATH=!USERPROFILE!\.dotnet;!PATH!"
)

rem --- Re-detect after the fallback. ---
if not defined DOTNET (
    where dotnet >nul 2>&1
    if not errorlevel 1 (
        dotnet --list-sdks 2>nul | "%FINDSTR%" /b "10." >nul 2>&1
        if not errorlevel 1 set "DOTNET=dotnet"
    )
)
if not defined DOTNET if exist "!USERPROFILE!\.dotnet\dotnet.exe" (
    "!USERPROFILE!\.dotnet\dotnet.exe" --list-sdks 2>nul | "%FINDSTR%" /b "10." >nul 2>&1
    if not errorlevel 1 set "DOTNET=!USERPROFILE!\.dotnet\dotnet.exe"
)
>> "!LOG!" echo [step] post-install DOTNET=[!DOTNET!]

if not defined DOTNET (
    >> "!LOG!" echo [step] SDK still missing - giving up
    echo.
    echo The .NET 10 SDK is still not available. Please install it manually:
    echo   https://aka.ms/dotnet/download
    echo then re-run install.cmd.
    echo.
    pause
    exit /b 1
)

rem --- Build. Append output to the log AND show it. ---
>> "!LOG!" echo [step] building with !DOTNET!
echo.
echo Using .NET SDK: !DOTNET!
echo.
echo Building Zephyr in Release - this may take a minute...
echo.
"!DOTNET!" build "%PROJECT%" -c Release --nologo >> "!LOG!" 2>&1
set "BUILDRC=!errorlevel!"
type "!LOG!"
if not "!BUILDRC!"=="0" (
    >> "!LOG!" echo [step] build FAILED rc=!BUILDRC!
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
    >> "!LOG!" echo [step] build ok but exe not found
    echo.
    echo Build reported success but Zephyr.exe was not found under %RELEASEDIR%.
    echo.
    pause
    exit /b 1
)

>> "!LOG!" echo [step] setup complete exe=!EXE!
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
