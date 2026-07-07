@echo off
setlocal

cd /d "%~dp0"

REM Activate venv
call venv\Scripts\activate.bat

REM Set model paths
set MODELS_DIR=%~dp0models
set HF_HOME=%MODELS_DIR%\huggingface
set SUDACHIDICT_DIR=%MODELS_DIR%\sudachi

if "%PORT%"=="" set PORT=8765

REM Check if port is already in use
netstat -ano | findstr /R /C:":%PORT% .*LISTENING" >nul 2>&1
if %ERRORLEVEL%==0 (
    echo Furigana Service already running on port %PORT%.
    exit /b 0
)

echo Starting Furigana Service on 127.0.0.1:%PORT%...
uvicorn server:app --host 127.0.0.1 --port %PORT%
