@echo off
setlocal

cd /d "%~dp0"

REM Activate venv
call venv\Scripts\activate.bat

REM Model paths -- override any of these to share models across machines.
REM Example: set MODELS_DIR=\\nas\share\models && run.bat
if "%MODELS_DIR%"=="" set MODELS_DIR=%~dp0models
if "%HF_HOME%"=="" set HF_HOME=%MODELS_DIR%\huggingface
if "%SUDACHIDICT_DIR%"=="" set SUDACHIDICT_DIR=%MODELS_DIR%\sudachi

REM FLFL model: HuggingFace model ID or absolute path to a local copy.
REM Example: set FLFL_MODEL=\\nas\share\models\huggingface\hub\models--Calvin-Xu--FLFL && run.bat
if "%FLFL_MODEL%"=="" set FLFL_MODEL=Calvin-Xu/FLFL

if "%PORT%"=="" set PORT=8765

REM Check if port is already in use
netstat -ano | findstr /R /C:":%PORT% .*LISTENING" >nul 2>&1
if %ERRORLEVEL%==0 (
    echo Furigana Service already running on port %PORT%.
    exit /b 0
)

echo Starting Furigana Service on 127.0.0.1:%PORT%...
echo   Models dir:    %MODELS_DIR%
echo   HF cache:      %HF_HOME%
echo   Sudachi dict:  %SUDACHIDICT_DIR%
echo   FLFL model:    %FLFL_MODEL%
uvicorn server:app --host 127.0.0.1 --port %PORT%
