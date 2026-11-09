@echo off
rem ClaudeCitavi mit Claude verbinden (Weg B, nach der Installation durch die IT). Ändert nur die eigene Claude-Konfiguration.
chcp 65001 >nul
set "PY="
where py >nul 2>nul && set "PY=py -3.12"
if not defined PY (
  echo Python 3.12 wurde nicht gefunden. Bitte die IT ansprechen.
  pause
  exit /b 1
)
%PY% "%~dp0einrichten.py" --modus nutzer
echo.
pause
