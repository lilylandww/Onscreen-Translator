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

echo Starting Furigana Service on 127.0.0.1:%PORT%...
uvicorn server:app --host 127.0.0.1 --port %PORT%
