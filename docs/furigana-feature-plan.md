# Furigana Feature Plan (Revised)

## Decision Summary

| Aspect | Decision |
|---|---|
| Morphological analyzer | **Sudachi** — via `sudachipy` (Python) hosted in an HTTP sidecar |
| Dictionary | **core (~50 MB)** |
| OOV fallback model | **FLFL** (`Calvin-Xu/FLFL`, 1B) — loaded via `transformers` + `bitsandbytes` (4-bit) in the same sidecar |
| Fallback degradation | Automatic switch to Ollama furigana prompt if FLFL CPU latency > 2s |
| Ruby rendering style | **Hiragana above each kanji** (classic ruby) |
| View toggle | **Both** — right-click context menu + `F` keyboard shortcut |
| Default view after OCR | **Translation** (current behavior preserved) |
| Sudachi delivery | **HTTP sidecar service** (single Python FastAPI process) |
| Existing furigana UI | **Remove** — `FuriganaLookup.cs`, `FuriganaPanel`, `PerformFuriganaLookup` deleted. Overlay replaces it. `FuriganaToggleButton` repurposed. |
| FLFL CPU validation spike | **Skipped** — build directly; degradation path (Ollama fallback) is the safety net |

---

## Alternatives Considered

Three viable furigana architectures were evaluated before choosing the sidecar. This section records why each was rejected, so the chosen path is justified rather than asserted.

| Option | Assessment | Rejected because |
|---|---|---|
| **Ollama-only** — prompt the existing OCR/translation LLM (gemma3:1b, gpt-4o-mini) for ruby output | Zero new dependencies, reuses existing HTTP plumbing, simplest to build. | Slower per-call (~200-500ms), lower quality for common words (no dictionary), inconsistent output format across models. The LLM must infer readings from training data rather than looking them up. |
| **Sudachi-only** — sidecar with Sudachi, no fallback | Fastest path (~30ms), cleanest sidecar (no torch/transformers). | Kanji without dictionary entries (rare names, slang, neologisms) get no reading. The whole point of furigana is to cover the edge cases a learner wouldn't know. |
| **Sudachi + Ollama fallback** | Medium complexity; Sudachi for dictionaries, Ollama for OOV. No FLFL download needed. | The user explicitly chose FLFL for the fallback role. FLFL is purpose-trained for furigana (1B params, LoRA on gpt-neox-japanese), while Ollama models are general-purpose. For OOV words, a specialized model should produce more reliable readings. |

The chosen path (Sudachi + FLFL) prioritizes **speed for common words** (Sudachi ~30ms) while preserving **specialized fallback quality** (FLFL) for edge cases. The 5GB FLFL download and torch/transformers dependency are accepted in exchange for dictionary-grade coverage on the critical path and a purpose-built model for OOV.

To guard against FLFL CPU latency risk, an **automatic degradation path** switches to Ollama if FLFL exceeds 2s per request.

---

## Migration of Existing Furigana Feature

The codebase has a working furigana side-panel that must be removed to avoid duplicate/conflicting features.

### Deletion list

| File/module | Lines | Rationale |
|---|---|---|
| `FuriganaLookup.cs` | 1–162 (entire file) | Replaced by the sidecar `/furigana` endpoint. The old implementation used crude word-splitting and WWWJDict with no morphological analysis. |
| `FuriganaPanel` (XAML block) | `MainWindow.xaml:396–479` | The side-panel list view is replaced by an inline overlay. No two furigana UIs. |
| `FuriganaToggleButton_Checked` / `_Unchecked` | `MainWindow.xaml.cs:1017–1027` | Old meaning: show/hide side panel. Deleted with the panel. |
| `PerformFuriganaLookup` | `MainWindow.xaml.cs:1163–1201` | Was the bridge between OCR output and `FuriganaLookup.cs`. Replaced by `IFuriganaProvider.GetFuriganaAsync`. |
| Furigana reference in `Canvas_MouseUp` | `MainWindow.xaml.cs:322–325` (`PerformFuriganaLookup(OCRText)`) | Removed; furigana is now fetched alongside translation, not after it. |

### Retention list (NOT deleted)

| File/module | Reason kept |
|---|---|
| `DictionaryLookup.cs` / `WWWJDict` | Still used by the Search feature at `MainWindow.xaml.cs:1133`. Only the furigana call path is migrated. |

### Repurposed: `FuriganaToggleButton`

Old meaning: **show/hide FuriganaPanel** (deleted).

New meaning: **toggle the overlay between Translation and Furigana view** for the current selection region. If no region exists, pressing it does nothing (perhaps with a brief tooltip hint). This is a single, unambiguous behavior — resolves the contradiction flagged in review.

---

## Architecture

```
┌───────────────────────────────────┐        HTTP (localhost:8765)
│  C# WPF App (existing)            │  ──────────────────────────────┐
│                                   │                                ▼
│  OCRText ──────► FuriganaClient ├──────► ┌──────────────────────────────────┐
│       │          (Providers/Furigana/)   │ Python FastAPI Sidecar            │
│       │                                  │ (furigana-service/)              │
│       ▼                                  │  ──────────────────────────────   │
│  Overlay: RubyTextBlock OR              │  GET  /health    → liveness      │
│           translatedTextBlock           │  GET  /status    → loaded flags  │
│           (toggled per region)          │  POST /furigana  → Sudachi (fast)│
│                                          │  POST /flfl      → FLFL (OOV)    │
│                                          │  POST /degrade   → fallback hint │
└───────────────────────────────────┘        └──────────────────────────────────┘
```

The sidecar is the single integration point. C# code only does HTTP + rendering — no Python/Rust interop in the .NET process.

### Why FLFL loads via `transformers` (not Ollama)

- No official GGUF exists; `Calvin-Xu/FLFL` is GPT-NeoX in safetensors (F32, ~5 GB).
- `transformers` + `bitsandbytes` loads it in 4-bit (~600 MB VRAM, runs on CPU too).
- Keeps both engines in one sidecar process — single port, single lifecycle.
- Future option: convert to GGUF and migrate to Ollama later (llama.cpp supports GPT-NeoX).

---

## New Files

### `furigana-service/` (new directory — the sidecar)

| File | Description |
|---|---|
| `server.py` | FastAPI app with routes: `/health`, `/status`, `/furigana`, `/flfl`, `/degrade` |
| `requirements.txt` | `sudachipy`, `sudachidict_core`, `jaconv`, `fastapi`, `uvicorn`, `transformers`, `torch`, `bitsandbytes`, `pydantic` |
| `install.bat` / `install.sh` | Create venv, `pip install -r requirements.txt`, pre-download Sudachi core dict + FLFL weights into `./models/` |
| `run.bat` / `run.sh` | Activate venv, `uvicorn server:app --host 127.0.0.1 --port 8765` |
| `models/` | Cached Sudachi dictionary + FLFL weights (downloaded on first run) |

#### `server.py` logic

**`POST /furigana`** (body: `{text, fallback?: bool=true}` → response: `{segments:[{surface, reading, pos, is_oov}]}`)

1. Run SudachiPy (mode A for fine-grained tokenization).
2. For each token: extract `surface`, `reading` (katakana — see conversion below), `pos`, `dict_id` (flag OOV if `-1`).
3. **Katakana → hiragana**: convert every `reading` using `jaconv.kata2hira()` before returning.
4. If `fallback==true` and any token has `is_oov==true` or empty reading:
   - Call FLFL on the full sentence.
   - Parse the FLFL ruby output (see parser spec below).
   - Merge FLFL character-level ruby back into the segment list, replacing only OOV segments.
5. Group consecutive kanji tokens within one Sudachi word → combine into one ruby group (full surface, full reading).
6. Return flat segment list.

**`POST /flfl`** (body: `{text}` → response: `{segments:[...], latency_ms: float}`)

- Direct FLFL inference using the prompt template:
  ```
  [INST] 次の文に正確に振り仮名を付けてください
  {sentence}
  [/INST]
  ```
  (Trailing `\n` after `[/INST]` matches the model card exactly.)
- Lazy-loads model on first call (loads 4-bit via `load_in_4bit=True`). Caches in memory.
- Parses FLFL output (spec below), applies `jaconv.kata2hira()`.
- Returns `latency_ms` so the C# sidecar manager can detect slowdown and auto-degrade.

**`POST /degrade`** (body: `{target: "ollama"}`)

- Instructs sidecar to stop lazily loading FLFL on future requests and return a no-fallback response instead (C# side must handle the "no FLFL" case by forwarding to Ollama itself).
- Used when the C# side detects FLFL latency > 2s and switches to `OllamaFuriganaFallbackProvider`.

**`GET /health`** → `{ok: true}`

**`GET /status`** → `{sudachi_ready: bool, flfl_loaded: bool, flfl_loading: bool, flfl_latency_ms: float | null}`

### FLFL output parser specification

Raw FLFL output example:

```
<ruby>国境<rt>くにざかい</rt></ruby>の<ruby>長<rt>なが</rt></ruby>いトンネルを<ruby>抜<rt>ぬ</rt></ruby>けると<ruby>雪国<rt>ゆきぐに</rt></ruby>であった<|endoftext|>
```

Parser rules:

1. Strip all special tokens: `<\|endoftext\|>`, `<\|im_start\|>`, `<\|im_end\|>`, pad tokens.
2. Match `<ruby>(.+?)<rt>(.*?)</rt></ruby>` pairs.
3. For segments not wrapped in `<ruby>`: emit as `{surface: text, reading: null, is_oov: false}`.
4. For `<ruby>` pairs: validate that concatenating all surface fragments reproduces the original input sentence (modulo whitespace normalization). If alignment fails → emit entire output as a single OOV segment with empty reading and log a warning.
5. Handle mixed-surface tokens where the FLFL output already includes trailing kana outside the ruby tag:
   ```
   <ruby>鰤<rt>ぶり</rt></ruby>ぶり   ← surface "鰤" but output shows "鰤ぶり"
   ```
   In such cases, the parser must detect the trailing kana duplication and emit `{surface: "鰤ぶり", reading: "ぶり"}` — the trailing kana is part of the surface, not a ruby annotation.
6. Apply `jaconv.kata2hira()` to all reading values before returning.

### C# side

| File | Description |
|---|---|
| `Providers/Furigana/IFuriganaProvider.cs` | Interface: `Task<List<FuriganaSegment>> GetFuriganaAsync(string text, bool allowFallback, CancellationToken)` |
| `Providers/Furigana/HttpFuriganaProvider.cs` | `HttpClient`-based implementation calling the sidecar `/furigana` |
| `Providers/Furigana/IFuriganaFallbackProvider.cs` | Interface for OOV fallback: `Task<List<FuriganaSegment>> GetFallbackAsync(string text, CancellationToken)` |
| `Providers/Furigana/FlflFallbackProvider.cs` | Calls sidecar `/flfl` (primary fallback) |
| `Providers/Furigana/OllamaFuriganaFallbackProvider.cs` | Calls the configured Ollama/OpenAI translation model with a furigana prompt (degradation path) |
| `Providers/Furigana/FuriganaSegment.cs` | Record: `string Surface, string? Reading, string Pos, bool IsOov` |
| `Controls/RubyTextBlock.cs` | Custom WPF control that lays out hiragana readings above kanji runs |
| `OverlayState.cs` | Per-region state tracking: `ocrText`, `translation`, `furiganaSegments`, `currentView` enum |
| `FuriganaServiceManager.cs` | Sidecar lifecycle: detect embedded Python, start/stop process, health-check, expose status, auto-degrade on FLFL slowdown |

---

## Modified Files

### `MainWindow.xaml`

- Add a `Controls:RubyTextBlock x:Name="rubyTextBlock"` sibling of `translatedTextBlock`, same canvas position, initially hidden.
- **Extend** the existing `<TextBox.ContextMenu>` at `MainWindow.xaml:99–106` (not replace). Insert new items above the existing Copy/Select All/Edit items:
  ```xml
  <MenuItem Header="Show Furigana" Click="ShowFurigana_Click" />
  <MenuItem Header="Show Translation" Click="ShowTranslation_Click" />
  <MenuItem Header="Show Original" Click="ShowOriginal_Click" />
  <Separator />
  <!-- existing Copy, Select All, Edit follow -->
  ```
- Remove the `FuriganaPanel` block at `MainWindow.xaml:396–479` and all references to `FuriganaResultsListBox`, `FuriganaCountText`.

### `MainWindow.xaml.cs`

- **Remove**: `FuriganaLookup` call at line 324, `PerformFuriganaLookup` method (lines 1163–1201), `FuriganaToggleButton_Checked`/`_Unchecked` (lines 1017–1027).
- **Repurpose `FuriganaToggleButton`**: Toggle overlay between Translation and Furigana view for the current region. (Single, unambiguous behavior.)
- **Add `_furiganaCts`**: `private CancellationTokenSource? _furiganaCts` at line 42 (alongside `_ocrCts`). Cancel + dispose at lines 165, 624, 944. Created fresh before each furigana request.
- Replace single global OCR/translation tracking with `OverlayState` for the current selection region.
- After OCR+translate (existing flow at line 322), also call `IFuriganaProvider.GetFuriganaAsync` with `_furiganaCts.Token` (non-blocking, parallel).
- Add `PreviewKeyDown` handler on the overlay controls (not global `Window.KeyDown`) for `F` key → cycle view.
- New rendering helper: `RenderRubyText(OverlayState)` — builds `RubyTextBlock` content.
  - Font-fit: use `CalculateOptimalFontSize` but pass a `rubyOverhead` parameter; subtract `fontSize * 0.55` per line from `effectiveHeight` when in furigana view.
- **"Show Original" view**: `translatedTextBlock.Text = OCRText` directly (no new control needed).
- Context-menu click handlers: `ShowFurigana_Click`, `ShowTranslation_Click`, `ShowOriginal_Click`.

### `AppSettings.cs`

Add `FuriganaSettings` block:
- `Enabled` (bool, default false)
- `SidecarUrl` (string, default `http://127.0.0.1:8765`)
- `AutoStartSidecar` (bool, default true)
- `UseFlflFallback` (bool, default true)
- `FuriganaPort` (int, default 8765)
- `FlflLatencyThresholdMs` (int, default 2000) — auto-degrade to Ollama above this

### `SettingsWindow.xaml` + `.cs`

Add "Furigana" tab with:
- Enable/disable checkbox
- Sidecar URL field
- "Test Connection" button (calls `/health`)
- "Install Sidecar" button → launches embedded Python + install process with progress bar
- "Start/Stop Sidecar" buttons (enable/disable only when sidecar installed)
- Status indicators: `sudachi_ready`, `flfl_loaded`, current latency
- Fallback degradation status: "FLFL" / "Ollama (degraded)"

---

## Ruby Rendering Design (`RubyTextBlock`)

WPF has no native ruby support. Approach: a custom `Panel` that arranges inline elements horizontally with wrapping, where each "word" is a vertical stack:

```
   ┌───────────┐
   │  くにざかい │  ← hiragana TextBlock (font ~50% of base, centered)
   │   国境     │  ← kanji TextBlock (normal font)
   └───────────┘
  の          長い          …
```

**Grouping rule** (matches printed Japanese convention):
- Consecutive kanji tokens belonging to one Sudachi word → one ruby group (full reading over full surface).
- Pure-kana tokens → rendered inline, no ruby stack.
- OOV tokens → ruby from FLFL per-character reading, or empty if fallback missing.

**Line-wrapping**: Use `WrapPanel` layout logic inside the custom control, or use `InlineUIContainer` entries inside a `TextBlock` (which gets wrapping for free but has nesting limits).

**Font-fit for ruby**: The existing `CalculateOptimalFontSize` (line 388) does not account for the extra row consumed by ruby text. When `OverlayState.ViewMode == Furigana`:
```
effectiveHeight -= fontSize * 0.55  // reserve space for ruby row per line
```
The `RubyTextBlock` also has its own measure override that adds the ruby row height to the total desired size.

---

## Toggle UX

Per-overlay state machine (`OverlayState.ViewMode`):

```
           ┌──────────────┐
           │  Translation  │  (default after OCR)
           └──────┬───────┘
                  │ right-click→"Show Furigana"  /  F key  /  FuriganaToggleButton ON
           ┌──────▼───────┐
           │   Furigana    │
           └──────┬───────┘
                  │ right-click→"Show Original"  /  F key
           ┌──────▼───────┐
           │    Original   │
           └──────┬───────┘
                  │ right-click→"Show Translation"  /  F key
                  │
                  └──→ Translation (cycle continues)
```

- **Default after OCR**: show Translation (unchanged behavior).
- **Right-click overlay → context menu**: `Show Furigana` / `Show Translation` / `Show Original`.
- **`F` key** when keyboard focus is on the overlay textbox (not global) → cycle through the three views.
- **`FuriganaToggleButton`** (repurposed): toggle between Translation and Furigana view for the current region. When toggled OFF, shows Translation. When toggled ON, shows Furigana. If no region exists, it does nothing.

---

## Sidecar Lifecycle

```
┌──────────┐      ┌─────────────────────┐      ┌──────────────────┐
│ App Start│─────►│ AutoStart           │─────►│ Poll /health     │
│          │      │ (if setting ON)     │      │ (timeout 30s)    │
└──────────┘      └─────────────────────┘      └──────────────────┘
                                                       │
                                                       ▼
                                                ┌──────────────────┐
                                                │ Service ready    │
                                                │ → Enable furigana│
                                                └──────────────────┘
```

- On app launch: if `Enabled && AutoStartSidecar` → `FuriganaServiceManager` spawns `run.bat`/`run.sh` as a hidden child process.
- On app exit: terminate child process.
- Manual control via Settings → "Start/Stop" buttons.
- If sidecar unreachable when furigana requested: overlay shows translation/OCR text with a brief "(furigana: service not running)" hint, falls back gracefully.
- On service crash/death: auto-restart with exponential backoff (3 attempts, delays: 1s, 4s, 15s).
- Auto-degradation: if `/flfl` latency exceeds `FlflLatencyThresholdMs` (default 2000ms) for 3 consecutive requests, C# calls `/degrade` and switches to `OllamaFuriganaFallbackProvider`. User can re-enable FLFL via Settings.

---

## Caching

| Layer | Strategy | Size |
|---|---|---|
| Sidecar (Sudachi) | In-memory LRU of `text → segments` | 1000 entries |
| Sidecar (FLFL) | In-memory LRU of `text → segments` | 100 entries (FLFL is expensive) |
| C# (`OverlayState`) | Cache per-region `furiganaSegments` until next OCR capture | 1 entry (single region) |

The sidecar LRU avoids re-running Sudachi or FLFL on repeated OCR of the same text (common when user re-selects the same area). Eviction is LRU with no TTL — dictionary readings don't go stale.

---

## Logging & Observability

- **Sidecar**: Writes to `%LOCALAPPDATA%/OnscreenTranslator/logs/furigana-service.log` with log rotation (max 10 MB, 3 backups). Log level: `INFO` by default, `DEBUG` in dev. Logs every request duration, every OOV fallback, and every FLFL load event.
- **C# side**: `FuriganaServiceManager` polls `/status` every 5s when furigana is enabled. Surfaces a health badge (green/yellow/red) in the toolbar next to the FuriganaToggleButton. Latency is logged to `System.Diagnostics.Trace` for debugging.
- **Crash reporting**: Sidecar process exit code + last 20 lines of stderr are captured and logged by `FuriganaServiceManager` on unexpected termination.

---

## Security Boundary

- The sidecar binds to `127.0.0.1` only (not `0.0.0.0`). No authentication or API key is needed — the localhost-only binding is the security boundary.
- The C# app communicates over plain HTTP (localhost loopback). No TLS needed.
- Sidecar receives only raw text strings — no images, no filesystem paths.
- The sidecar has no access to the C# app's API keys or settings.

---

## Performance Budget

| Step | Target | Notes |
|---|---|---|
| Sidecar `/furigana` (Sudachi only, no OOV) | **< 30 ms** | Already-loaded dict in Python process, simple JSON round-trip |
| Sidecar `/furigana` (with FLFL OOV fallback, GPU) | **< 800 ms cold, < 400 ms warm** | FLFL lazy-loaded on first OOV; cached after warm-up. GPU assumed. |
| Sidecar `/flfl` (direct, GPU) | **< 400 ms** | Only fires for OOV path. GPU required for this budget. |
| C# HTTP round-trip + render | **< 30 ms** | Localhost + WPF measure/arrange included |
| **Total (typical, no OOV)** | **~60 ms** | Imperceptible user delay |
| **Total (with OOV, GPU)** | **~450 ms** | Only for rare OOV words |
| **Total (with OOV, CPU — degradation path)** | **~varied** | Auto-switches to Ollama fallback; latency = Ollama LLM speed for that model |

**Note**: The FLFL 4-bit CPU latency is **speculative** — no validation spike was run by user decision. If real-world latency exceeds 2s, the automatic degradation path to Ollama will activate. The `OllamaFuriganaFallbackProvider` should not exceed the configured translation model's typical latency (~200-500ms).

---

## Pre-Mortem: Failure Scenarios & Mitigations

| # | Failure | Likelihood | Impact | Mitigation |
|---|---|---|---|---|
| 1 | **FLFL CPU > 2s** on typical CPU | Medium | User sees 2+ second pause after OCR | Auto-degrade to Ollama after 3 slow calls. User can re-enable FLFL. |
| 2 | **Sidecar won't start** (Python missing, port in use) | Medium | Furigana unavailable | Graceful fallback: show translation with "(furigana: service not running)" hint. Settings tab shows error log excerpt. |
| 3 | **Sidecar crashes mid-session** | Low | Furigana stops mid-session | Auto-restart × 3 with backoff. If all fail, disable furigana and prompt user to check Settings. |
| 4 | **FLFL output unparseable** for a given sentence | Low-Medium | OOV segment gets empty reading | Parser falls back: sentences that fail alignment validation emit the full sentence as a single segment with null reading (no ruby displayed). Logged at WARN level. |
| 5 | **Ruby text overflows overlay bounds** | Medium | Text clipped, unreadable | Font-size binary search in `CalculateOptimalFontSize` already handles overflow; the `* 0.55` overhead for ruby rows is subtracted. If still overflowing, fall back to plain text without ruby. |
| 6 | **Model download fails mid-install** | Medium | First-run install hangs | Install script downloads with resume support. If interrupted, retry with exponential backoff (3 attempts). Progress bar in Settings shows per-file status. "Cancel" button kills the download. |

---

## Test Plan

### Unit tests

| What | How |
|---|---|
| `FuriganaSegment` construction | Verify record fields, nullability of `Reading` for kana surfaces. |
| Katakana→hiragana conversion | `jaconv.kata2hira("クニザカイ") == "くにざかい"`; verify no-op for hiragana-only strings. |
| FLFL ruby parser — nominal case | Input: `<ruby>国境<rt>くにざかい</rt></ruby>の` → `[{surface:"国境", reading:"くにざかい"}, {surface:"の", reading:null}]` |
| FLFL ruby parser — with `<\|endoftext\|>` | Verify token is stripped, not emitted as a segment. |
| FLFL ruby parser — mixed surface | Input: `<ruby>鰤<rt>ぶり</rt></ruby>ぶり` → `[{surface:"鰤ぶり", reading:"ぶり"}]` |
| FLFL ruby parser — alignment failure | Input: `<ruby>foo<rt>bar</rt></ruby>baz` when original is `xyz` → fallback to single OOV segment with empty reading. |
| Ruby grouping logic | Input segments: `[{surface:"国", reading:"くに"},{surface:"境", reading:"ざかい"}]` with `IsOov:false` and consecutive `pos` → group to `[{surface:"国境", reading:"くにざかい"}]`. |
| `CalculateOptimalFontSize` with ruby overhead | Verify the `* 0.55` adjustment reduces `effectiveHeight` proportionally. |

### Integration tests

| What | How |
|---|---|
| Sidecar `/health` returns 200 | `curl http://127.0.0.1:8765/health` → `{ok: true}` |
| Sidecar `/furigana` with Japanese text | `curl -X POST -H 'content-type: application/json' -d '{"text":"国境の長いトンネル"}' http://127.0.0.1:8765/furigana` → verify `segments` array, no `is_oov:true` for common words, all readings in hiragana. |
| Sidecar `/furigana` with pure kana text | Readings all null, no OOV flags. |
| Sidecar `/furigana` with garbage/ASCII text | No crash, returns empty segments or segments with empty reading. |
| Sidecar `/flfl` with OOV kanji | Verify FLFL loads on first call (latency > 0), returns parseable segments. |
| Sidecar `/status` before and after `/flfl` | Before: `flfl_loaded: false`. After: `flfl_loaded: true`. |
| `HttpFuriganaProvider` with mock HTTP | Verify deserialization of sidecar response into `List<FuriganaSegment>`. Verify cancellation via `CancellationToken`. |
| `OllamaFuriganaFallbackProvider` | Mock `HttpClient` returning a known furigana-like response; verify parsing. |

### End-to-end tests (manual, documented)

| Scenario | Steps |
|---|---|
| Basic furigana flow | Select region → OCR → translation appears → toggle to furigana → ruby text above kanji → toggle back to translation |
| Right-click cycle | Right-click translation overlay → "Show Furigana" → right-click → "Show Original" → right-click → "Show Translation" |
| F key cycle | Select region → press F → furigana → press F → original → press F → translation |
| Sidecar unavailable | Kill sidecar process → select region → translation appears normally, "(furigana: service not running)" hint is visible but unobtrusive |
| First-run install | Enable furigana in Settings → "Install Sidecar" → progress bar → completion → "Start Sidecar" → health OK |
| CPU degradation | On a CPU-only machine with FLFL slow > 2s → overlay shows furigana but uses Ollama → Settings status shows "Ollama (degraded)" |

### Observability verification

| What | How |
|---|---|
| Sidecar log file created | Check `%LOCALAPPDATA%/OnscreenTranslator/logs/furigana-service.log` after sidecar start |
| Log rotation works | Verify file doesn't exceed 10 MB |
| Health badge in toolbar | Green when `/status` returns all ready; yellow when FLFL not loaded; red when sidecar unreachable |

---

## Open Risks & Mitigations

| Risk | Mitigation |
|---|---|
| **FLFL CPU vs GPU speed** | 4-bit on CPU is slow (unverified, but user declined validation spike). Auto-degrade to Ollama after 3 slow calls > 2s. |
| **Sidecar port conflicts** | Default 8765; configurable in Settings; fallback to next available port if 8765 is occupied, with `SidecarUrl` updated automatically. |
| **Ruby line-height in font-fitting** | `effectiveHeight -= fontSize * 0.55` per line when in furigana view. Ruby measure is part of `RubyTextBlock` layout override. |
| **FLFL training quality** | Only ~1 epoch on Aozora fiction → may give incorrect readings for modern vocabulary. Sudachi handles common words; FLFL only for OOV. If FLFL quality is poor for a user, they can manually select "Ollama fallback" in Settings. |
| **First-run sidecar install** | Embedded Python 3.12 (~30 MB) bundled in installer. On first "Enable Furigana", `install.bat` runs from the embedded Python: creates venv, `pip install`s deps, downloads Sudachi core (~50 MB) + FLFL 4-bit (~600 MB). Progress bar in Settings shows per-file status. "Cancel" button kills the download. Total download: ~650 MB (one-time). |
| **Sidecar process management** | Use `Process` with `UseShellExecute=false`, redirect stdout/stderr to log. Kill via `process.CloseMainWindow()` + `process.Kill()` on timeout (5s grace). Track PID for orphan cleanup in `App.xaml.cs` shutdown. |
| **WWWJDict still used by search** | `DictionaryLookup.cs` / `WWWJDict` is NOT deleted — it powers the Search feature. Only `FuriganaLookup.cs` is removed. |

---

## Implementation Order (Branches)

Each phase is a separate `agent/<task>` branch, branched from a committed `main`:

| # | Branch | Delivers |
|---|---|---|
| 1 | `agent/furigana-sidecar` | `furigana-service/` directory — Python FastAPI with SudachiPy + FLFL. Routes: `/health`, `/status`, `/furigana`, `/flfl`, `/degrade`. Includes FLFL parser, jaconv conversion, LRU cache, logging. Testable via curl. **No C# changes.** |
| 2 | `agent/furigana-cs-client` | C# `IFuriganaProvider`, `HttpFuriganaProvider`, `IFuriganaFallbackProvider`, `FlflFallbackProvider`, `OllamaFuriganaFallbackProvider`, `FuriganaServiceManager`, new `AppSettings.FuriganaSettings`. Deletes `FuriganaLookup.cs`. Repurposes `FuriganaToggleButton`. Side-panel furigana is gone; consumers use the provider interface instead. |
| 3 | `agent/furigana-ruby-control` | `RubyTextBlock` custom control + `OverlayState`. Image capture→OCR→furigana fetched in parallel with `_furiganaCts`. Ruby overlay renders alongside/hiding translation. Font-fit includes ruby row overhead. |
| 4 | `agent/furigana-toggle-ux` | Context-menu items inserted at `MainWindow.xaml:99–106` (Show Furigana/Translation/Original). `F` key handler on overlay controls. View cycling. |
| 5 | `agent/furigana-settings-ui` | Settings tab: enable/disable, install sidecar (with progress bar), start/stop, health indicators, degradation status. |
| 6 | `agent/furigana-flfl-fallback` | Wire FLFL into `/furigana` OOV path; implement auto-degradation (latency > 2s → call `/degrade` → switch to `OllamaFuriganaFallbackProvider`); end-to-end verification. |
