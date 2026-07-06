#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

echo "=== Furigana Service Installer ==="

# Create venv
if [ ! -d venv ]; then
    echo "[1/5] Creating virtual environment..."
    python3 -m venv venv
else
    echo "[1/5] Virtual environment already exists, skipping creation."
fi

# Activate venv
echo "[2/5] Activating venv and installing dependencies..."
# shellcheck disable=SC1091
source venv/bin/activate
pip install --upgrade pip -q
pip install -r requirements.txt

# Set up models directory -- override MODELS_DIR to use a shared/network path.
# Example: MODELS_DIR=/mnt/nas/models ./install.sh
export MODELS_DIR="${MODELS_DIR:-$SCRIPT_DIR/models}"
export HF_HOME="${HF_HOME:-$MODELS_DIR/huggingface}"
export SUDACHIDICT_DIR="${SUDACHIDICT_DIR:-$MODELS_DIR/sudachi}"
mkdir -p "$HF_HOME" "$SUDACHIDICT_DIR"

# Download Sudachi core dictionary
echo "[3/5] Downloading Sudachi core dictionary..."
python3 -c "
import sudachipy
from sudachipy import Dictionary
print('Sudachi dictionary ready.')
" 2>/dev/null || echo "Note: SudachiPy dict download handled on first startup."

# Pre-download FLFL model weights
echo "[4/5] Downloading FLFL model weights (~600 MB, first time only)..."
python3 -c "
from transformers import AutoTokenizer, AutoModelForCausalLM
import os
model_id = 'Calvin-Xu/FLFL'
cache = os.path.join('$HF_HOME', 'hub')
print(f'Downloading tokenizer to {cache}...')
AutoTokenizer.from_pretrained(model_id, cache_dir=cache)
print('Tokenizer downloaded.')
print('Note: Model weights will be downloaded on first /flfl call.')
print('      To pre-download, run: python3 -c \"from transformers import AutoModelForCausalLM; AutoModelForCausalLM.from_pretrained(\\\"Calvin-Xu/FLFL\\\", cache_dir=\\\"' + cache + '\\\")\"')
" || echo "Warning: FLFL download skipped (will download on first use)."

echo "[5/5] Writing env vars to run.sh defaults..."
# Ensure run.sh is executable
chmod +x run.sh 2>/dev/null || true

echo ""
echo "=== Installation complete ==="
echo "Run './run.sh' to start the service."
echo ""
echo "To share models across machines, set these before running:"
echo "  export MODELS_DIR=/path/to/shared/models"
echo "  export FLFL_MODEL=/path/to/local/FLFL  (or a HuggingFace model ID)"
