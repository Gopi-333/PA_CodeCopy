@echo off
rem Builds dist\PaCodeCopy-py.exe (no Python needed on the PCs that run it).
cd /d "%~dp0"
python -m pip install --upgrade pyinstaller || goto :error
python -m PyInstaller --noconfirm --onefile --windowed --name PaCodeCopy-py pa_codecopy.py || goto :error
echo.
echo Built: %~dp0dist\PaCodeCopy-py.exe
pause
exit /b 0

:error
echo.
echo Build failed. Is Python 3.10+ installed and on PATH? (python --version)
pause
exit /b 1
