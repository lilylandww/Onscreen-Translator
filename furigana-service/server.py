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
    flfl_model: str


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

# FLFL model: HuggingFace model ID or absolute path to a local copy.
# Set via FLFL_MODEL env var.  Example: FLFL_MODEL=/mnt/nas/models/FLFL
FLFL_MODEL = os.environ.get("FLFL_MODEL", "Calvin-Xu/FLFL")


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
    """Lazy-load the FLFL model on first use. Returns True on success.

    Thread-safe: only one thread performs the actual load. Others spin-wait
    outside the lock until loading completes.
    """
    global _flfl_model, _flfl_tokenizer_obj, _flfl_loaded, _flfl_loading

    if _degraded:
        logger.info("FLFL load skipped -- service degraded")
        return False

    with _flfl_lock:
        if _flfl_loaded:
            return True
        if _flfl_loading:
            # Another thread is currently loading -- fall through to spin-wait
            pass
        else:
            _flfl_loading = True
            try:
                trf = _import_transformers()
                _ = _import_torch()
                _ = _import_bitsandbytes()
                logger.info("Loading FLFL tokenizer from %s ...", FLFL_MODEL)
                _flfl_tokenizer_obj = trf.AutoTokenizer.from_pretrained(FLFL_MODEL)
                logger.info("Loading FLFL model (4-bit, device_map=auto) from %s ...", FLFL_MODEL)
                _flfl_model = trf.AutoModelForCausalLM.from_pretrained(
                    FLFL_MODEL,
                    load_in_4bit=True,
                    device_map="auto",
                )
                _flfl_loaded = True
                logger.info("FLFL model loaded successfully")
            except Exception:
                logger.exception("Failed to load FLFL model")
                _flfl_model = None
                _flfl_tokenizer_obj = None
                _flfl_loaded = False
            finally:
                _flfl_loading = False
            return _flfl_loaded

    # We reach here when _flfl_loading was True (another thread is loading).
    # Spin-wait outside the lock.
    while _flfl_loading:
        time.sleep(0.1)
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
            pos = ','.join(token.part_of_speech())

            # OOV detection via dict_id (=-1 for out-of-vocabulary words).
            # Fall back to heuristic if dict_id is unavailable in this
            # sudachipy version.
            try:
                word_info = token.get_word_info()
                dict_id = word_info.dict_id
                is_oov = (dict_id == -1)
            except (AttributeError, Exception):
                # Fallback: only flag as OOV if surface has kanji and no reading
                is_oov = False  # conservative default

            reading_form = token.reading_form()
            reading = jaconv.kata2hira(reading_form) if reading_form else None

            # Secondary heuristic: if still not flagged OOV but surface has
            # kanji and no reading, treat as OOV.
            if not is_oov and not reading and _has_kanji(surface):
                is_oov = True

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
    """Merge adjacent kanji segments belonging to the same POS into one.

    Only merges when:
    - Both surfaces contain kanji
    - Both have the same POS string
    - Neither is OOV (OOV segments stay separate for individual FLFL fallback)
    """
    if not segments:
        return segments

    grouped: List[FuriganaSegment] = []
    i = 0
    while i < len(segments):
        curr = segments[i]
        if _has_kanji(curr.surface) and not curr.is_oov:
            surfaces = [curr.surface]
            readings = [curr.reading or '']
            pos = curr.pos
            is_oov = curr.is_oov
            j = i + 1
            while (
                j < len(segments)
                and _has_kanji(segments[j].surface)
                and not segments[j].is_oov
                and segments[j].pos == pos
            ):
                surfaces.append(segments[j].surface)
                readings.append(segments[j].reading or '')
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
    """Replace OOV segments with FLFL readings via character-offset mapping.

    FLFL readings are distributed proportionally across the surface characters
    of each FLFL segment, so that a multi-character kanji surface like
    "国境" with reading "くにざかい" maps each surface character to its
    share of the reading rather than assigning the full reading to each char.
    """
    if not flfl_segments or (len(flfl_segments) == 1 and flfl_segments[0].is_oov):
        return segments

    # Build character -> reading map from FLFL segments using proportional split.
    # Each surface character gets a slice of the reading proportional to its
    # position within the surface.
    char_readings: dict[int, str] = {}
    offset = 0
    for seg in flfl_segments:
        if seg.reading and seg.surface:
            surf_len = len(seg.surface)
            read_len = len(seg.reading)
            for i in range(surf_len):
                start = int(i * read_len / surf_len)
                end = int((i + 1) * read_len / surf_len)
                char_readings[offset + i] = seg.reading[start:end]
        offset += len(seg.surface)

    result: List[FuriganaSegment] = list(segments)
    offset = 0
    for i, seg in enumerate(result):
        if seg.is_oov or not seg.reading:
            reading_chars = []
            for j in range(len(seg.surface)):
                if (offset + j) in char_readings:
                    reading_chars.append(char_readings[offset + j])
            merged_reading = ''.join(reading_chars)
            if merged_reading:
                result[i] = FuriganaSegment(
                    surface=seg.surface,
                    reading=merged_reading,
                    pos=seg.pos,
                    is_oov=False,
                )
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
        flfl_model=FLFL_MODEL,
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


@app.post("/reset-degradation", response_model=DegradeResponse)
async def reset_degradation() -> DegradeResponse:
    global _degraded
    _degraded = False
    logger.info("Degradation reset -- FLFL re-enabled")
    return DegradeResponse(ok=True, degraded=False)


@app.post("/shutdown")
async def shutdown_route():
    logger.info("Shutdown requested via API")
    def self_destruct():
        time.sleep(0.2)
        os._exit(0)
    threading.Thread(target=self_destruct, daemon=True).start()
    return {"ok": True}


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
