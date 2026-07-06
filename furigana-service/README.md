# Furigana Service

FastAPI sidecar that provides Japanese furigana (phonetic reading) annotation for the Onscreen-Translator WPF application. Runs on `localhost` only (security boundary).

## Architecture

- **SudachiPy** (mode A, fine-grained) handles dictionary-based morphological analysis.
- **FLFL** (`Calvin-Xu/FLFL`) is a purpose-trained 1B-param LLM loaded in 4-bit quantization as an OOV (out-of-vocabulary) fallback.
- Both engines live in a single FastAPI process on port 8765.

## Setup

### Prerequisites

- Python 3.10+ (3.12 recommended)
- ~650 MB disk for models (Sudachi core dict ~50 MB, FLFL weights ~600 MB)

### Install

```bash
# Linux / macOS
chmod +x install.sh
./install.sh

# Windows
install.bat
```

This creates a `venv/`, installs all dependencies, and pre-downloads the Sudachi dictionary and FLFL weights into `models/`.

### Run

```bash
# Linux / macOS
chmod +x run.sh
./run.sh

# Windows
run.bat
```

The service starts on `http://127.0.0.1:8765` (configurable via `PORT` env var).

## API Contract

### `GET /health`

Liveness probe.

```json
{"ok": true}
```

### `GET /status`

Service status.

```json
{
  "sudachi_ready": true,
  "flfl_loaded": false,
  "flfl_loading": false,
  "flfl_latency_ms": null
}
```

### `POST /furigana`

Annotate text with furigana readings. Sudachi handles dictionary words; FLFL is used as fallback for OOV tokens when `fallback=true`.

**Request:**

```json
{"text": "国境の長いトンネル", "fallback": true}
```

**Response:**

```json
{
  "segments": [
    {"surface": "国境", "reading": "くにざかい", "pos": "名詞", "is_oov": false},
    {"surface": "の", "reading": null, "pos": "助詞", "is_oov": false},
    {"surface": "長い", "reading": "ながい", "pos": "形容詞", "is_oov": false},
    {"surface": "トンネル", "reading": null, "pos": "名詞", "is_oov": false}
  ]
}
```

All readings are hiragana (katakana converted via `jaconv`).

### `POST /flfl`

Direct FLFL inference (forces model load on first call).

**Request:** `{"text": "..."}`

**Response:**

```json
{
  "segments": [...],
  "latency_ms": 342.5
}
```

### `POST /degrade`

Signal the sidecar to stop using FLFL and fall back to Ollama externally.

**Request:** `{"target": "ollama"}`

**Response:** `{"ok": true, "degraded": true}`

## Caching

- Sudachi results: in-memory LRU, 1000 entries
- FLFL results: in-memory LRU, 100 entries

## Logging

Logs are written to:
- **Linux/macOS:** `./logs/furigana-service.log`
- **Windows:** `%LOCALAPPDATA%/OnscreenTranslator/logs/furigana-service.log`

Rotation: 10 MB max, 3 backups.

## Troubleshooting

| Symptom | Fix |
|---|---|
| `sudachidict_core` not found | Run `install.sh`/`install.bat` to download the dictionary |
| FLFL load fails / OOM | Ensure ~2 GB RAM free; check GPU availability for `bitsandbytes` |
| Port 8765 in use | Set `PORT=8766` env var before running |
| `ModuleNotFoundError: torch` | Activate the venv before running: `source venv/bin/activate` |
