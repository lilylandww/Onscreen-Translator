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

# Check if port is already in use
if command -v lsof >/dev/null 2>&1; then
    if lsof -i ":${PORT}" -sTCP:LISTEN -t >/dev/null 2>&1; then
        echo "Furigana Service already running on port ${PORT}."
        exit 0
    fi
elif command -v ss >/dev/null 2>&1; then
    if ss -tln | grep -q ":${PORT} "; then
        echo "Furigana Service already running on port ${PORT}."
        exit 0
    fi
fi

echo "Starting Furigana Service on 127.0.0.1:${PORT}..."
exec uvicorn server:app --host 127.0.0.1 --port "$PORT"
