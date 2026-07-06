"""
Furigana Service -- FastAPI sidecar for Onscreen-Translator.

Provides morphological analysis (SudachiPy) and OOV fallback (FLFL model)
for Japanese furigana generation over localhost HTTP.
"""

import os
import re
import sys
import time
import logging
import threading
from collections import OrderedDict
from logging.handlers import RotatingFileHandler
from typing import List, Optional

import jaconv
from pydantic import BaseModel

# ---------------------------------------------------------------------------
# Logging
# ---------------------------------------------------------------------------

_log_dir: str
if sys.platform == "win32":
    _local = os.environ.get("LOCALAPPDATA", os.path.expanduser("~"))
    _log_dir = os.path.join(_local, "OnscreenTranslator", "logs")
else:
    _log_dir = os.path.join(os.path.dirname(os.path.abspath(__file__)), "logs")

os.makedirs(_log_dir, exist_ok=True)
_log_file = os.path.join(_log_dir, "furigana-service.log")

logger = logging.getLogger("furigana-service")
logger.setLevel(logging.INFO)

_console = logging.StreamHandler(sys.stdout)
_console.setLevel(logging.INFO)
_console.setFormatter(logging.Formatter("%(asctime)s [%(levelname)s] %(message)s"))
logger.addHandler(_console)

_file_handler = RotatingFileHandler(
    _log_file, maxBytes=10 * 1024 * 1024, backupCount=3
)
_file_handler.setLevel(logging.INFO)
_file_handler.setFormatter(logging.Formatter("%(asctime)s [%(levelname)s] %(message)s"))
logger.addHandler(_file_handler)

# ---------------------------------------------------------------------------
# Lazy heavy imports -- module parses without torch/transformers
# ---------------------------------------------------------------------------

_sudachipy = None
_transformers = None
_torch = None
_bitsandbytes = None


def _import_sudachipy():
    global _sudachipy
    if _sudachipy is None:
        import sudachipy as _mod
        _sudachipy = _mod
    return _sudachipy


def _import_transformers():
    global _transformers
    if _transformers is None:
        import transformers as _mod
        _transformers = _mod
    return _transformers


def _import_torch():
    global _torch
    if _torch is None:
        import torch as _mod
        _torch = _mod
    return _torch


def _import_bitsandbytes():
    global _bitsandbytes
    if _bitsandbytes is None:
        import bitsandbytes as _mod
        _bitsandbytes = _mod
    return _bitsandbytes


# ---------------------------------------------------------------------------
# Pydantic request / response models
# ---------------------------------------------------------------------------


class HealthResponse(BaseModel):
    ok: bool = True


class StatusResponse(BaseModel):
    sudachi_ready: bool
    flfl_loaded: bool
    flfl_loading: bool
    flfl_latency_ms: Optional[float] = None


class FuriganaSegment(BaseModel):
    surface: str
    reading: Optional[str] = None
    pos: str
    is_oov: bool


class FuriganaRequest(BaseModel):
    text: str
    fallback: bool = True


class FuriganaResponse(BaseModel):
    segments: List[FuriganaSegment]


class FlflRequest(BaseModel):
    text: str


class FlflResponse(BaseModel):
    segments: List[FuriganaSegment]
    latency_ms: float


class DegradeRequest(BaseModel):
    target: str


class DegradeResponse(BaseModel):
    ok: bool
    degraded: bool


# ---------------------------------------------------------------------------
# LRU cache (thread-safe, dict-based)
# ---------------------------------------------------------------------------


class LRUCache:
    """Thread-safe LRU cache with a fixed maximum size."""

    def __init__(self, capacity: int):
        self.capacity = capacity
        self._data: OrderedDict = OrderedDict()
        self._lock = threading.Lock()

    def get(self, key: str):
        with self._lock:
            if key in self._data:
                self._data.move_to_end(key)
                return self._data[key]
        return None

    def put(self, key: str, value):
        with self._lock:
            if key in self._data:
                self._data.move_to_end(key)
            self._data[key] = value
            while len(self._data) > self.capacity:
                self._data.popitem(last=False)


_sudachi_cache: LRUCache = LRUCache(1000)
_flfl_cache: LRUCache = LRUCache(100)

# ---------------------------------------------------------------------------
# Module-level state
# ---------------------------------------------------------------------------

_sudachi_tokenizer = None
_sudachi_ready = False

_flfl_model = None
_flfl_tokenizer_obj = None
_flfl_loaded = False
_flfl_loading = False
_flfl_latency_ms: Optional[float] = None
_flfl_lock = threading.Lock()

_degraded = False  # when True, never attempt FLFL


# ---------------------------------------------------------------------------
# Sudachi initialisation
# ---------------------------------------------------------------------------


def _init_sudachi() -> None:
    """Initialise SudachiPy with mode A (fine-grained). Called at startup."""
    global _sudachi_tokenizer, _sudachi_ready
    try:
        spd = _import_sudachipy()
        _sudachi_tokenizer = spd.Dictionary().create()
        _sudachi_ready = True
        logger.info("SudachiPy initialised (mode A)")
    except Exception:
        logger.exception("Failed to initialise SudachiPy")
        _sudachi_ready = False


# ---------------------------------------------------------------------------
# FLFL initialisation (lazy, guarded by lock)
# ---------------------------------------------------------------------------


def _init_flfl() -> bool:
    """Lazy-load the FLFL model on first use. Returns True on success."""
    global _flfl_model, _flfl_tokenizer_obj, _flfl_loaded, _flfl_loading

    if _degraded:
        logger.info("FLFL load skipped -- service degraded")
        return False

    with _flfl_lock:
        if _flfl_loaded:
            return True
        if _flfl_loading:
            # Another thread is currently loading -- spin-wait.
            _flfl_lock.release()
            while _flfl_loading:
                time.sleep(0.1)
            _flfl_lock.acquire()
            return _flfl_loaded

        _flfl_loading = True
        logger.info("Loading FLFL model (may take a while)...")
        logger.info("FLFL load event: starting model download / load")

        try:
            trf = _import_transformers()
            _import_torch()
            _import_bitsandbytes()

            model_id = "Calvin-Xu/FLFL"
            _flfl_tokenizer_obj = trf.AutoTokenizer.from_pretrained(model_id)
            _flfl_model = trf.AutoModelForCausalLM.from_pretrained(
                model_id,
                load_in_4bit=True,
                device_map="auto",
            )
            _flfl_loaded = True
            logger.info("FLFL model loaded successfully")
            logger.info("FLFL load event: model ready")
        except Exception:
            logger.exception("Failed to load FLFL model")
            _flfl_loaded = False
        finally:
            _flfl_loading = False

        return _flfl_loaded
