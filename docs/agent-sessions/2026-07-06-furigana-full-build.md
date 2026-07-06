# Session Summary — 2026-07-06 Furigana Feature (Full Build)

## Goal
Implement the complete furigana feature for Onscreen-Translator per `docs/furigana-feature-plan.md`:
- Python FastAPI sidecar (SudachiPy morphological analyzer + FLFL 4-bit LLM for OOV fallback)
- C# WPF overlay rendering hiragana ruby above kanji, with view toggling
- Auto-degradation to Ollama when FLFL is slow

## Approach
Orchestrated 6 implementation phases via parallel coder subagents, then a code-review pass, then targeted fixes.

## Files Changed (vs master before feature)

### New — Python sidecar (`furigana-service/`)
- `server.py` (624→~700 lines) — FastAPI app, SudachiPy tokenization, FLFL lazy-load (4-bit), ruby parser with alignment validation, katakana→hiragana conversion, LRU caches, `/health` `/status` `/furigana` `/flfl` `/degrade` `/reset-degradation` routes, 127.0.0.1-only binding
- `tests/test_server.py` — 21 pytest tests (parser, kata2hira, grouping, merge, API)
- `requirements.txt`, `install.{sh,bat}`, `run.{sh,bat}`, `README.md`, `.gitignore`

### New — C# providers (`Providers/Furigana/`)
- `IFuriganaProvider.cs`, `HttpFuriganaProvider.cs`
- `IFuriganaFallbackProvider.cs`, `OllamaFuriganaFallbackProvider.cs` (degradation path)
- `FuriganaSegment.cs` (record with snake_case JSON attributes)

### New — C# infrastructure
- `FuriganaServiceManager.cs` — sidecar process lifecycle, health polling, auto-restart (3 backoff), degradation monitor (Timer-based, lock-protected)
- `Controls/RubyTextBlock.cs` — custom WPF control rendering ruby via InlineUIContainer + StackPanel
- `OverlayState.cs` — per-region state (OcrText, Translation, FuriganaSegments, CurrentView)

### Modified
- `AppSettings.cs` — added `FuriganaSettings` (Enabled, SidecarUrl, AutoStartSidecar, UseFlflFallback, FuriganaPort, FlflLatencyThresholdMs)
- `MainWindow.xaml` — removed `FuriganaPanel`; added `rubyTextBlock`; shared `OverlayContextMenu` resource; `PreviewKeyDown` on overlays
- `MainWindow.xaml.cs` — removed `PerformFuriganaLookup`/old toggle handlers; added `FetchFuriganaAsync` (parallel, `_furiganaCts`-cancelled, gated on Enabled), `RenderOverlay()`, view cycling, auto-start in `Window_Loaded`, Ollama fallback merge, `DisposeFuriganaResources()`
- `SettingsWindow.{xaml,xaml.cs}` — Furigana tab (enable/url/autostart/useflfl + Test Connection/Install/Start/Stop/Status)

### Deleted
- `FuriganaLookup.cs` (162 lines — replaced by sidecar)
- `Providers/Furigana/FlflFallbackProvider.cs` (dead code, removed in review fixes)

### Kept (NOT deleted)
- `DictionaryLookup.cs` / `WWWJDict` — still used by Search feature at `MainWindow.xaml.cs:1552`

## Commands Run
- `dotnet build` — 0 errors, 0 warnings (verified after every phase + after fixes)
- `python3 -m pytest furigana-service/tests/ -v` — 21 passed
- `grep`/`rg` verifications for dangling references, 0.0.0.0 binding, deleted-file cleanup

## Code Review
One pass via `code-reviewer` subagent. Verdict: **REVISE** (2 CRITICAL + 12 MAJOR + 13 MINOR).

### Fixed (2 CRITICAL + 9 MAJOR)
- **C1**: `_init_flfl()` was a stub — never loaded the model. Now calls `AutoModelForCausalLM.from_pretrained("Calvin-Xu/FLFL", load_in_4bit=True)`.
- **C2**: No auto-start in `Window_Loaded`. Added `Task.Run(StartAsync)` when `Enabled && AutoStartSidecar`.
- **M1**: Added `POST /reset-degradation` route.
- **M2**: `FetchFuriganaAsync` gated on `_settings.Furigana.Enabled`.
- **M3**: `_group_consecutive_kanji` respects POS boundaries + skips OOV.
- **M4**: OOV detection via `dict_id == -1` instead of empty reading.
- **M5**: Deleted dead `FlflFallbackProvider.cs`.
- **M6**: Unsubscribed singleton events on window close.
- **M7**: `DisposeFuriganaResources()` called from both `Quit()` and `Window_Closing`.
- **M8**: `_merge_flfl_into_oov` splits readings proportionally.
- **M9**: `ParseRubyOutput` strips special tokens + alignment validation.
- **M10**: `MergeLlmFallback` uses exact surface equality.
- **M11**: Degradation timer synchronized via `lock(_lock)` + `volatile`.

### Deferred (MINOR — follow-up)
- m1: Inconsistent kanji detection ranges between Python/C#
- m6: `RubyTextBlock` brushes not frozen
- m13: Inconsistent `HttpClient` timeouts
- M14: No C# unit tests (Python tests only)
- M12: `RubyTextBlock.OnBaseFontSizeChanged` doesn't rebuild inlines
- M13: `JsonSerializerOptions` not shared

## Architecture Decisions
- **Sudachi + FLFL** chosen over Ollama-only (slower, lower quality) and Sudachi-only (no OOV coverage)
- **HTTP sidecar** chosen over bundled CLI (cleaner IPC, single lifecycle)
- **Embedded Python + first-run download** for distribution (~650 MB: Sudachi core + FLFL 4-bit)
- **Auto-degradation** to Ollama after 3 consecutive FLFL calls >2s (safety net for CPU-only machines)

## Branches
- `agent/furigana-plan` — plan document (merged to master first)
- `agent/furigana-ruby-control` — phases 1+3 (sidecar + ruby control)
- `agent/furigana-settings-ui` — phases 4+5 (toggle UX + settings)
- `agent/furigana-flfl-fallback` — phase 6 + all review fixes
- All merged to `master` at `78ae8d1`

## Known Limitations / Future Work
- FLFL CPU latency is unvalidated (no spike was run per user decision). Auto-degradation is the safety net.
- FLFL trained only ~1 epoch on Aozora fiction; may give wrong readings for modern vocabulary.
- C# unit test coverage is zero (M14). Python covers the parser + API shape only.
- No MSI/MSIX packaging yet for the embedded Python + model download (install.bat/run.bat are manual).
- First-run UX requires Python 3.10+ available on PATH for the install script.
