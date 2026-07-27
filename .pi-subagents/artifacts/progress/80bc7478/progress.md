# Core Fixes Progress

## Status: COMPLETE

All 11 fixes from the code review have been applied and verified.

### Fixes Applied

| # | Component | Change | Status |
|---|---|---|---|
| 1 | DictionaryLookup.cs | Made LookUp/GetSearchResults properly async (no more sync-over-async deadlock) | ✅ |
| 2 | ProviderFactory.cs | Added "remote" to AvailableTranslationProviders array | ✅ |
| 3 | ProviderFactory.cs | CreateOcrProvider default case now throws ArgumentException | ✅ |
| 4 | ProviderFactory.cs | CreateTranslationProviders (plural) now adds RemoteTranslationProvider | ✅ |
| 5 | RemoteOCRProvider.cs + RemoteTranslationProvider.cs | IsAvailableAsync now checks /api/health | ✅ |
| 6 | FuriganaServiceManager.cs | OnProcessExited wrapped in try-catch | ✅ |
| 7 | DeepLTranslationProvider.cs | CancellationToken passed through to TranslateTextAsync | ✅ |
| 8 | OcrOutputCleaner.cs (new) | Extracted duplicated CleanOcrOutput from Ollama/OpenAI providers | ✅ |
| 9 | AppSettings.cs ProtectedStringConverter | Per-installation entropy (machine name hash), XOR fallback always used | ✅ |
| 10 | AppSettings.cs RemoteServerConfig | Simplified to Enabled + BaseUrl + ApiKey, removed dead routing props | ✅ |
| 11 | Interfaces/IModelListable.cs (new) | Moved IModelListable to own file | ✅ |

### Build Verification

- Core: ✅ 0 warnings, 0 errors
- Linux: ✅ 0 warnings, 0 errors
- Server: ✅ 0 warnings, 0 errors
- WPF: ✅ 0 warnings, 0 errors
