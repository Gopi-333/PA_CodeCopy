@echo off
rem Starts PA CodeCopy (Python version) without a console window.
cd /d "%~dp0"
where pythonw >nul 2>nul && (start "" pythonw pa_codecopy.py & exit /b 0)
python pa_codecopy.py
if errorlevel 1 pause
