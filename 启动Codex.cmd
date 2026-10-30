@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\open-codex.ps1"
if errorlevel 1 pause
