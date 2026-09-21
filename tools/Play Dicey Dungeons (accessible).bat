@echo off
REM Starts Dicey Dungeons with the accessibility launcher.
REM Put a shortcut to this on the desktop.
setlocal
set HERE=%~dp0
start "" "%HERE%..\companion\bin\Release\net9.0-windows\win-x64\DiceyA11y.exe"
