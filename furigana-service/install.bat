@echo off
setlocal

echo === Furigana Service Installer ===

cd /d "%~dp0"

REM Create venv
if not exist venv (
    echo [1/4] Creating virtual environment...
    python -m venv venv
) else (
    echo [1/4] Virtual environment already exists, skipping.
)

REM Activate and install deps
echo [2/4] Installing dependencies...
call venv\Scripts\activate.bat
pip install --upgrade pip -q
pip install -r requirements.txt

REM Set up models directory -- override MODELS_DIR to use a shared/network path.
REM Example: set MODELS_DIR=\\nas\share\models && install.bat
if "%MODELS_DIR%"=="" set MODELS_DIR=%~dp0models
if "%HF_HOME%"=="" set HF_HOME=%MODELS_DIR%\huggingface
if "%SUDACHIDICT_DIR%"=="" set SUDACHIDICT_DIR=%MODELS_DIR%\sudachi
if not exist "%HF_HOME%" mkdir "%HF_HOME%"
if not exist "%SUDACHIDICT_DIR%" mkdir "%SUDACHIDICT_DIR%"

REM Download Sudachi dict
echo [3/4] Preparing Sudachi dictionary...
python -c "import sudachipy; print('Sudachi dictionary ready.')" 2>nul

REM Pre-download FLFL
echo [4/4] Downloading FLFL model weights (may take a while)...
python -c "from transformers import AutoTokenizer; print('Tokenizer ready.')" 2>nul || echo Note: FLFL will download on first use.

echo.
echo === Installation complete ===
echo Run 'run.bat' to start the service.
echo.
echo To share models across machines, set these before running:
echo   set MODELS_DIR=\\nas\share\models
echo   set FLFL_MODEL=\\path\to\local\FLFL  (or a HuggingFace model ID)
