@echo off
python -X utf8 "%~dp0adapters\enhancement-mode\enhancement_mode.py" enhanced --workspace "%~dp0."
if errorlevel 1 pause
