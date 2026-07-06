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


# ---------------------------------------------------------------------------
# FLFL output parser
# ---------------------------------------------------------------------------


def parse_flfl_output(raw_text: str, original_text: str) -> List[FuriganaSegment]:
    """Parse FLFL ruby-tagged output into a list of FuriganaSegment.

    Works standalone with no model loaded — pure string processing.
    Follows the spec in docs/furigana-feature-plan.md § FLFL output parser.
    """
    # 1. Strip special tokens
    cleaned = re.sub(
        r'<\|endoftext\|>|<\|im_start\|>|<\|im_end\|>', '', raw_text
    )

    # 2. Walk left-to-right, matching <ruby>SURF<rt>READ</rt></ruby> or literal text
    ruby_pattern = re.compile(r'<ruby>(.+?)<rt>(.*?)</rt></ruby>')
    segments: List[FuriganaSegment] = []
    last_end = 0

    for m in ruby_pattern.finditer(cleaned):
        # Literal text before this ruby tag
        literal = cleaned[last_end:m.start()]
        if literal:
            segments.append(
                FuriganaSegment(surface=literal, reading=None, pos='', is_oov=False)
            )

        surf = m.group(1)
        reading = jaconv.kata2hira(m.group(2))
        segments.append(
            FuriganaSegment(surface=surf, reading=reading, pos='', is_oov=False)
        )
        last_end = m.end()

    # Trailing literal text after the last ruby tag
    trailing = cleaned[last_end:]
    if trailing:
        segments.append(
            FuriganaSegment(surface=trailing, reading=None, pos='', is_oov=False)
        )

    if not segments:
        return [
            FuriganaSegment(surface=original_text, reading=None, pos='', is_oov=True)
        ]

    # 4. Alignment check
    surfaces = [s.surface for s in segments]
    joined = re.sub(r'\s+', '', ''.join(surfaces))
    orig_norm = re.sub(r'\s+', '', original_text)
    if joined != orig_norm:
        logger.warning(
            "FLFL alignment mismatch: parsed=%r original=%r", joined, orig_norm
        )
        return [
            FuriganaSegment(
                surface=original_text, reading=None, pos='', is_oov=True
            )
        ]

    # 5. Merge trailing kana duplication:
    #    When a ruby segment is immediately followed by a literal segment whose
    #    surface equals the ruby reading, the trailing kana is part of the surface.
    #    e.g.  <ruby>鰤<rt>ぶり</rt></ruby>ぶり  →  surface "鰤ぶり", reading "ぶり"
    merged: List[FuriganaSegment] = []
    i = 0
    while i < len(segments):
        curr = segments[i]
        if (
            curr.reading is not None
            and i + 1 < len(segments)
            and segments[i + 1].reading is None
            and segments[i + 1].surface == curr.reading
        ):
            merged.append(
                FuriganaSegment(
                    surface=curr.surface + segments[i + 1].surface,
                    reading=curr.reading,
                    pos=curr.pos,
                    is_oov=curr.is_oov,
                )
            )
            i += 2
        else:
            merged.append(curr)
            i += 1

    return merged


# ---------------------------------------------------------------------------
# Sudachi-to-segments helper
# ---------------------------------------------------------------------------


def _sudachi_to_segments(text: str) -> List[FuriganaSegment]:
    """Tokenize *text* with SudachiPy (mode A) and return FuriganaSegments."""
    try:
        if not _sudachi_ready or _sudachi_tokenizer is None:
            return [
                FuriganaSegment(surface=text, reading=None, pos='', is_oov=True)
            ]

        tokens = _sudachi_tokenizer.tokenize(text)
        segments: List[FuriganaSegment] = []
        for token in tokens:
            surface = token.surface()
            reading_form = token.reading_form()
            reading = jaconv.kata2hira(reading_form) if reading_form else None
            pos = ','.join(token.part_of_speech())
            is_oov = not bool(reading)
            segments.append(
                FuriganaSegment(
                    surface=surface, reading=reading, pos=pos, is_oov=is_oov
                )
            )

        return segments if segments else [
            FuriganaSegment(surface=text, reading=None, pos='', is_oov=True)
        ]
    except Exception:
        logger.exception("Sudachi tokenization failed")
        return [FuriganaSegment(surface=text, reading=None, pos='', is_oov=True)]


# ---------------------------------------------------------------------------
# Kanji detection & consecutive-kanji grouping
# ---------------------------------------------------------------------------


def _has_kanji(s: str) -> bool:
    """Return True if *s* contains any CJK Unified Ideograph."""
    for ch in s:
        cp = ord(ch)
        if 0x4E00 <= cp <= 0x9FFF or 0x3400 <= cp <= 0x4DBF:
            return True
    return False


def _group_consecutive_kanji(
    segments: List[FuriganaSegment],
) -> List[FuriganaSegment]:
    """Merge adjacent segments whose surfaces both contain kanji into one."""
    if not segments:
        return segments

    grouped: List[FuriganaSegment] = []
    i = 0
    while i < len(segments):
        curr = segments[i]
        if _has_kanji(curr.surface):
            surfaces = [curr.surface]
            readings = [curr.reading or '']
            pos = curr.pos
            is_oov = curr.is_oov
            j = i + 1
            while j < len(segments) and _has_kanji(segments[j].surface):
                surfaces.append(segments[j].surface)
                readings.append(segments[j].reading or '')
                is_oov = is_oov or segments[j].is_oov
                j += 1
            if j > i + 1:
                merged_reading = ''.join(readings) if any(readings) else None
                grouped.append(
                    FuriganaSegment(
                        surface=''.join(surfaces),
                        reading=merged_reading,
                        pos=pos,
                        is_oov=is_oov,
                    )
                )
            else:
                grouped.append(curr)
            i = j
        else:
            grouped.append(curr)
            i += 1

    return grouped


# ---------------------------------------------------------------------------
# FLFL merge helper for /furigana OOV fallback
# ---------------------------------------------------------------------------


def _merge_flfl_into_oov(
    segments: List[FuriganaSegment],
    flfl_segments: List[FuriganaSegment],
) -> List[FuriganaSegment]:
    """Replace OOV segments with FLFL readings via character-offset mapping."""
    if not flfl_segments or (len(flfl_segments) == 1 and flfl_segments[0].is_oov):
        return segments

    # Build character → reading map from FLFL segments
    char_readings: dict[int, str] = {}
    offset = 0
    for seg in flfl_segments:
        if seg.reading:
            for i in range(len(seg.surface)):
                char_readings[offset + i] = seg.reading
        offset += len(seg.surface)

    result: List[FuriganaSegment] = []
    offset = 0
    for seg in segments:
        if seg.is_oov or not seg.reading:
            reading = char_readings.get(offset)
            if reading:
                result.append(
                    FuriganaSegment(
                        surface=seg.surface,
                        reading=reading,
                        pos=seg.pos,
                        is_oov=False,
                    )
                )
            else:
                result.append(seg)
        else:
            result.append(seg)
        offset += len(seg.surface)

    return result


# ---------------------------------------------------------------------------
# FLFL inference helper (used by /furigana OOV path)
# ---------------------------------------------------------------------------


def _run_flfl(text: str) -> List[FuriganaSegment]:
    """Run FLFL model and return parsed segments.  Never raises."""
    try:
        _init_flfl()
        if (
            not _flfl_loaded
            or _flfl_model is None
            or _flfl_tokenizer_obj is None
        ):
            return [
                FuriganaSegment(surface=text, reading=None, pos='', is_oov=True)
            ]

        prompt = (
            f"[INST] 次の文に正確に振り仮名を付けてください\n{text}\n[/INST]\n"
        )
        torch = _import_torch()
        inputs = _flfl_tokenizer_obj.encode(prompt, return_tensors="pt")
        device = _flfl_model.device
        inputs = inputs.to(device)
        outputs = _flfl_model.generate(
            inputs, max_new_tokens=512, do_sample=False
        )
        # Extract only the generated tokens (skip the prompt prefix)
        generated_ids = outputs[0][inputs.shape[1]:]
        decoded = _flfl_tokenizer_obj.decode(
            generated_ids, skip_special_tokens=False
        )
        return parse_flfl_output(decoded, text)
    except Exception:
        logger.exception("FLFL inference failed")
        return [FuriganaSegment(surface=text, reading=None, pos='', is_oov=True)]


# ---------------------------------------------------------------------------
# FastAPI application
# ---------------------------------------------------------------------------

from fastapi import FastAPI, HTTPException  # noqa: E402
from contextlib import asynccontextmanager  # noqa: E402


@asynccontextmanager
async def lifespan(app):
    _init_sudachi()
    yield


app = FastAPI(title="Furigana Service", lifespan=lifespan)


@app.get("/health")
async def health() -> HealthResponse:
    return HealthResponse(ok=True)


@app.get("/status")
async def status() -> StatusResponse:
    return StatusResponse(
        sudachi_ready=_sudachi_ready,
        flfl_loaded=_flfl_loaded,
        flfl_loading=_flfl_loading,
        flfl_latency_ms=_flfl_latency_ms,
    )


@app.post("/furigana", response_model=FuriganaResponse)
async def furigana_route(req: FuriganaRequest) -> FuriganaResponse:
    # 1. Cache check
    cached = _sudachi_cache.get(req.text)
    if cached is not None:
        return FuriganaResponse(segments=cached)

    # 2. Sudachi tokenize
    segments = _sudachi_to_segments(req.text)

    # 3. Optional FLFL fallback for OOV tokens
    if req.fallback and not _degraded:
        has_oov = any(s.is_oov or not s.reading for s in segments)
        if has_oov:
            logger.info(
                "OOV detected, running FLFL fallback for: %s", req.text[:80]
            )
            flfl_segments = _run_flfl(req.text)
            segments = _merge_flfl_into_oov(segments, flfl_segments)

    # 4. Group consecutive kanji
    segments = _group_consecutive_kanji(segments)

    # 5. Cache and return
    _sudachi_cache.put(req.text, segments)
    return FuriganaResponse(segments=segments)


@app.post("/flfl", response_model=FlflResponse)
async def flfl_route(req: FlflRequest) -> FlflResponse:
    if _degraded:
        raise HTTPException(status_code=503, detail="degraded")

    _init_flfl()
    if not _flfl_loaded:
        raise HTTPException(status_code=503, detail="FLFL model not loaded")

    # Cache check
    cached = _flfl_cache.get(req.text)
    if cached is not None:
        return cached

    # Build prompt with EXACT template (trailing \n after [/INST])
    prompt = (
        f"[INST] 次の文に正確に振り仮名を付けてください\n{req.text}\n[/INST]\n"
    )

    start = time.time()
    torch = _import_torch()
    inputs = _flfl_tokenizer_obj.encode(prompt, return_tensors="pt")
    device = _flfl_model.device
    inputs = inputs.to(device)
    outputs = _flfl_model.generate(
        inputs, max_new_tokens=512, do_sample=False
    )
    generated_ids = outputs[0][inputs.shape[1]:]
    decoded = _flfl_tokenizer_obj.decode(
        generated_ids, skip_special_tokens=False
    )
    latency_ms = (time.time() - start) * 1000

    global _flfl_latency_ms
    _flfl_latency_ms = latency_ms

    segments = parse_flfl_output(decoded, req.text)
    response = FlflResponse(segments=segments, latency_ms=latency_ms)

    _flfl_cache.put(req.text, response)
    logger.info("FLFL completed in %.1f ms for: %s", latency_ms, req.text[:80])
    return response


@app.post("/degrade", response_model=DegradeResponse)
async def degrade_route(req: DegradeRequest) -> DegradeResponse:
    global _degraded
    _degraded = True
    logger.info("Service degraded per request (target=%s)", req.target)
    return DegradeResponse(ok=True, degraded=True)


# ---------------------------------------------------------------------------
# Entrypoint
# ---------------------------------------------------------------------------

if __name__ == "__main__":
    import uvicorn

    uvicorn.run(
        app,
        host="127.0.0.1",
        port=int(os.environ.get("PORT", "8765")),
    )
