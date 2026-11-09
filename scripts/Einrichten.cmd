@echo off
rem ClaudeCitavi einrichten (Weg C: Citavi-Makro). Startet einrichten.py mit Python 3.12.
chcp 65001 >nul
set "PY="
where py >nul 2>nul && set "PY=py -3.12"
if not defined PY if exist "%LOCALAPPDATA%\Programs\Python\Python312\python.exe" set "PY="%LOCALAPPDATA%\Programs\Python\Python312\python.exe""
if not defined PY (
  echo Python 3.12 wurde nicht gefunden. Bitte zuerst von python.org installieren.
  pause
  exit /b 1
)
%PY% "%~dp0einrichten.py" --modus makro
echo.
pause
