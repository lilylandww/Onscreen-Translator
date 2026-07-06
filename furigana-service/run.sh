#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

# Activate venv
# shellcheck disable=SC1091
source venv/bin/activate

# Model paths -- override any of these to share models across machines.
# Example: MODELS_DIR=/mnt/nas/models ./run.sh
export MODELS_DIR="${MODELS_DIR:-$SCRIPT_DIR/models}"
export HF_HOME="${HF_HOME:-$MODELS_DIR/huggingface}"
export SUDACHIDICT_DIR="${SUDACHIDICT_DIR:-$MODELS_DIR/sudachi}"

# FLFL model: HuggingFace model ID or absolute path to a local copy.
# Example: FLFL_MODEL=/mnt/nas/models/huggingface/hub/models--Calvin-Xu--FLFL ./run.sh
export FLFL_MODEL="${FLFL_MODEL:-Calvin-Xu/FLFL}"

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
echo "  Models dir:    $MODELS_DIR"
echo "  HF cache:      $HF_HOME"
echo "  Sudachi dict:  $SUDACHIDICT_DIR"
echo "  FLFL model:    $FLFL_MODEL"
exec uvicorn server:app --host 127.0.0.1 --port "$PORT"
