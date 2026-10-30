@echo off
if exist "%~dp0CodexControlCenter.App.exe" (
 start "" "%~dp0CodexControlCenter.App.exe"
) else (
 start "" "%~dp0release\v0.9.0\CodexControlCenter\CodexControlCenter.App.exe"
)
