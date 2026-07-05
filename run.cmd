@echo off
setlocal enabledelayedexpansion
cd /d "%~dp0"

set "RELEASEDIR=Zephyr.UI\bin\Release"

rem --- Launch the built exe if it exists (the everyday path) ---
set "EXE="
for /r "%RELEASEDIR%" %%F in (Zephyr.exe) do if not defined EXE if exist "%%F" set "EXE=%%F"
if defined EXE (
    start "" "!EXE!"
    exit /b 0
)

rem --- Not built yet: point the user at first-time setup ---
echo Zephyr hasn't been set up on this PC yet.
echo Run install.cmd once to install prerequisites and build Zephyr.
echo.
set "ANSWER="
set /p "ANSWER=Run setup now? [Y/N] "
if /i not "!ANSWER!"=="Y" exit /b 0

call "%~dp0install.cmd"

rem --- After setup, launch if the build produced an exe ---
set "EXE="
for /r "%RELEASEDIR%" %%F in (Zephyr.exe) do if not defined EXE if exist "%%F" set "EXE=%%F"
if defined EXE start "" "!EXE!"
exit /b 0
