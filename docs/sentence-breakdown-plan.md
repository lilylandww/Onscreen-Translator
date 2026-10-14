# Sentence Breakdown Plan (Hoverable Vocab + Grammar)

## Decision Summary

| Aspect | Decision |
|---|---|
| Breakdown source | **One structured LLM call per sentence** (JSON parts), fetched in background after OCR |
| UI surface | **New `OverlayViewMode.Breakdown`** view in the F-key cycle + context menu |
| Popup content | **Minimal** — reading (kana) + meaning (English gloss / grammar function) |
| Chunking | Non-overlapping spans; concatenated surfaces must equal the OCR text (grammar patterns like 〜なければならない are one part) |
| Renderer | Reuse `RubyTextBlock` (adds `SetBreakdownParts` + hover popup) |
| Fallback | No parts (disabled/failed/in-flight) → render original OCR text, **keep** Breakdown view mode so the F-cycle continues past it |
| Alignment failure | Strict whitespace-insensitive concatenation check → empty list → fallback (mirrors `OllamaFuriganaFallbackProvider.ParseRubyOutput`) |
| Settings | `breakdown: { enabled, provider, model }` — provider ∈ ollama/openai/openai-compatible; empty model → provider's translation model |

## Alternatives Considered

| Option | Rejected because |
|---|---|
| **Hybrid Sudachi + LLM enrichment** | Reliable boundaries but two systems to orchestrate per capture; LLM-only keeps one call with full control over grammar-chunk grouping (Sudachi tokens are words, not grammar units). |
| **Lazy per-token lookup on hover** | Popups show spinners; many small calls; poor scan experience. |
| **Hover on Furigana view** | Clutters the existing view; a dedicated view keeps the F-cycle modes clean. |
| **Rich popup (POS, conjugation, dictionary link)** | User chose minimal reading+meaning; keeps popup a lightweight tooltip. |

## Architecture

```
OCR text ──► FetchBreakdownAsync (fire-and-forget, _breakdownCts)
                │
                ▼
        LLMSentenceBreakdownProvider  POST {baseUrl}/chat/completions
                │   (Ollama / OpenAI / OpenAI-compatible — same DTOs as
                │    OllamaFuriganaFallbackProvider, proven /chat/completions path)
                ▼
        BreakdownParser.Parse(output, text)   ← pure: strip fences, JSON extract,
                │                               exact alignment validation
                ▼
        OverlayState.BreakdownParts : List<BreakdownPart>
                │
                ▼  (CurrentView == Breakdown)
        rubyTextBlock.SetBreakdownParts(...)  → per-part hover:
            highlight tint + dotted underline (grammar) + delayed Popup
            (reading line + meaning line, non-interactive)
```

## Data Model

`Providers/Breakdown/BreakdownPart.cs`

```csharp
public enum BreakdownPartKind { Vocab, Grammar }
public record BreakdownPart(string Surface, string? Reading, string Meaning, BreakdownPartKind Kind);
```

## Files

| File | Action |
|---|---|
| `Providers/Breakdown/BreakdownPart.cs` | new — record + kind enum |
| `Providers/Breakdown/ISentenceBreakdownProvider.cs` | new — `BreakdownAsync(text, ct)` |
| `Providers/Breakdown/BreakdownParser.cs` | new — pure parse + alignment validation |
| `Providers/Breakdown/LLMSentenceBreakdownProvider.cs` | new — chat-completions client + prompt |
| `Providers/ProviderFactory.cs` | `AvailableBreakdownProviders`, display names, `CreateBreakdownProvider` |
| `AppSettings.cs` | `BreakdownSettings` + `Breakdown` property |
| `OverlayState.cs` | `Breakdown` enum member + `BreakdownParts` |
| `Controls/RubyTextBlock.cs` | `SetBreakdownParts`, hover highlight, delayed Popup |
| `MainWindow.xaml` | context-menu `Show Breakdown` |
| `MainWindow.xaml.cs` | fetch/render/F-cycle/cancel-on-settings-change/clear-on-recapture |
| `SettingsWindow.xaml(.cs)` | Sentence Breakdown card (enable/provider/model/refresh) |
| `docs/agent-sessions/2026-10-15-sentence-breakdown.md` | session summary |

## Prompt Contract

System: split Japanese sentences into learner-facing parts; reply with ONLY a JSON array.

User output shape: `[{"s":"国境","r":"こっきょう","m":"border","t":"v"}, {"s":"の","r":null,"m":"possessive particle","t":"g"}, …]`

- `s` concatenated must reproduce the input exactly (validated, whitespace-insensitive)
- `r` only when `s` contains kanji, else null
- `m` ≤ ~6-word English gloss; grammar = its function
- `t`: `v` vocab | `g` grammar (particles, auxiliaries, endings, set patterns)

## Risks / Mitigations

- **LLM JSON drift** → strict alignment validation; empty result falls back to rendering the OCR text in the Breakdown view (view mode preserved).
- **Small models produce poor JSON** → dedicated `breakdown.model` setting; default reuses translation model (users can point at a larger one).
- **Popup lingering across view switches** → popup closes on part MouseLeave, `IsVisibleChanged` of the control, and on every `SetSegments`/`SetBreakdownParts` rebuild.

## Verification

- `dotnet build WpfAppTest.csproj` — bar: 0 warnings / 0 errors (no C# test infra exists)
- `python3 -m pytest furigana-service/tests/ -q` — sidecar untouched, sanity
- Runtime/hover behavior requires Windows (WPF); validated by build here + manual run on Windows.

## Pre-existing Build Fix (this branch)

`FuriganaServiceManager.StopAsync()` (master `4680ca8`) was `async Task` but ended with
`return Task.CompletedTask;` — CS1997, master did not compile. Removed the invalid return
(commit `fix: remove invalid return value in async StopAsync breaking build on master`).
Stale gitignored `OnscreenTranslator.{Core,Linux,Server}/bin|obj` artifacts (absent from git)
were also deleted locally to allow the single-project build.
