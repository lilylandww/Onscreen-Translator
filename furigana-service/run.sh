#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

# Activate venv
# shellcheck disable=SC1091
source venv/bin/activate

# Set model paths
export MODELS_DIR="$SCRIPT_DIR/models"
export HF_HOME="$MODELS_DIR/huggingface"
export SUDACHIDICT_DIR="$MODELS_DIR/sudachi"

PORT="${PORT:-8765}"

echo "Starting Furigana Service on 127.0.0.1:${PORT}..."
exec uvicorn server:app --host 127.0.0.1 --port "$PORT"
