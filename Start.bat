@echo off
cd /d "%~dp0"
pwsh -NoProfile -File "%~dp0scripts\build.ps1" %*
