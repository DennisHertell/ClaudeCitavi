@echo off
rem ClaudeCitavi ohne Adminrechte einrichten (Weg C). Doppelklick genügt.
chcp 65001 >nul
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0makro-einrichten.ps1"
echo.
pause
