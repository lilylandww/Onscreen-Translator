Now I have a complete picture. Let me compile the thorough review.

---

## Review

### Correct: What Is Already Good (with evidence)

1. **Interface design is clean and minimal**: `IOCRProvider`, `ITranslationProvider`, `IFuriganaProvider`, `IFuriganaFallbackProvider`, `IModelListable` are all well-scoped with proper `CancellationToken` support. Files: `OnscreenTranslator.Core/Interfaces/IOCRProvider.cs:12-19`, `ITranslationProvider.cs:10-16`.

2. **Provider implementations are generally solid**: `OllamaOCRProvider` (364 LOC) has robust retry logic, LLM output cleaning, deduplication. `OpenAIOCRProvider` has an excellent system prompt for faithful OCR. All providers handle errors with try-catch and descriptive failure messages.

3. **FuriganaServiceManager process lifecycle is well-managed**: Health polling with timeout, graceful shutdown with kill fallback, auto-restart with exponential backoff, and a 600ms debounced status event to prevent UI flicker. File: `OnscreenTranslator.Core/Services/FuriganaServiceManager.cs`.

4. **Degradation monitoring is sound**: Consecutive slow-FLFL-count threshold with proper locking and volatile fields. `StartDegradationMonitoring`/`StopDegradationMonitoring` properly manage the timer lifecycle. File: `FuriganaServiceManager.cs:397-432`.

5. **AppSettings defensive patterns**: `EnsureDefaultProviders` merges defaults, `GetProviderConfig` creates-on-miss, `ProtectedStringConverter` handles backward-compatible encrypted/plaintext detection. File: `OnscreenTranslator.Core/Settings/AppSettings.cs`.

6. **Core library builds clean**: `dotnet build` succeeds with 0 warnings, 0 errors.

---

### Fixed: Issues, Locations, and Resolutions

*(No edits applied — this is a review-only task.)*

---

### Blocker: Critical Issues That Must Be Resolved

1. **`WWWJDict` uses sync-over-async (`.Wait()` / `.Result`)** — will deadlock if called from a UI thread (WPF dispatcher or Avalonia UI thread). File: `OnscreenTranslator.Core/Services/DictionaryLookup.cs:17-23`.
   ```
   responseTask.Wait();
   HttpResponseMessage response = responseTask.Result;
   ```
   **Fix**: Make `LookUp` async, or use `ConfigureAwait(false)` with `GetAwaiter().GetResult()` in a background thread. Better yet, make `GetSearchResults` async too.

2. **`AvailableTranslationProviders` is missing `"remote"`** — inconsistent with `TranslationProviderDisplayNames` (which includes it) and `CreateTranslationProvider` (which handles it). The UI dropdown for translation providers will not show "Remote Server". File: `OnscreenTranslator.Core/Providers/ProviderFactory.cs:18`.
   ```csharp
   public static readonly string[] AvailableTranslationProviders = ["google", "deepl", "ollama", "openai", "openai-compatible"];
   ```
   **Fix**: Add `"remote"` to the array.

3. **`RemoteOCRProvider.IsAvailableAsync()` is a no-op** — just checks `_http.BaseAddress != null` which is always true after construction. A server that is down will report as "available". File: `OnscreenTranslator.Core/Providers/RemoteOCRProvider.cs:46-54`. Same for `RemoteTranslationProvider.cs:52-60`.
   **Fix**: Actually attempt an HTTP GET to `/api/health` and return whether it succeeds.

4. **`OnProcessExited` is `async void` with unguarded `await IsHealthyAsync()`** — if the HTTP call throws an unexpected exception, it will crash the process since `async void` exceptions are unobserved. File: `FuriganaServiceManager.cs:335`.
   **Fix**: Wrap the entire body in try-catch.

---

### Major: Design Flaws and Significant Issues

5. **HttpClient anti-pattern across all providers** — Every provider creates its own `HttpClient` instance. This wastes sockets, ignores DNS TTL, and can cause socket exhaustion. 10 separate HttpClient instances are created across providers. Only `GoogleTranslationProvider` correctly uses a static shared instance.
   **Fix**: Use `IHttpClientFactory` or a shared static `HttpClient` per provider type.

6. **`ProtectedStringConverter` XOR fallback is not encryption** — The comment says "better than plaintext" but XOR with a hardcoded static key (`OnscreenTranslator-ApiKey-Protection-v1`) provides zero security. Anyone with the source can reverse it. File: `OnscreenTranslator.Core/Settings/AppSettings.cs:113-118`.
   **Fix**: Use AES-GCM for the fallback, or remove the fallback and throw a clear error if platform protection is unavailable.

7. **Encryption failure silently stores plaintext** — In `ProtectedStringConverter.Write` (line ~110-117), if `PlatformServices.ProtectData` throws, the catch block writes the API key in cleartext. This is a security regression with no user notification.
   **Fix**: At minimum, log a WARNING. Better: re-throw or disable save.

8. **`DeepLTranslationProvider.TranslateAsync` doesn't pass `CancellationToken`** — The `TranslateTextAsync` call ignores the token. File: `OnscreenTranslator.Core/Providers/DeepLTranslationProvider.cs:27`.

9. **Duplicated `CleanOcrOutput`** — Nearly identical 60-line method exists in both `OllamaOCRProvider.cs:205-273` and `OpenAIOCRProvider.cs:182-246`. The only difference is OllamaOCRProvider wraps the result with `DeduplicateOcrOutput`.
   **Fix**: Extract to a shared `OcrOutputCleaner` utility class.

10. **`BaseUrl` property setters mutate HttpClient.BaseAddress after construction** — If the BaseUrl is set after the HttpClient is created (lazy init), the BaseAddress changes while the client may be in use. Not thread-safe. Files: `OllamaOCRProvider.cs:30-35`, `OpenAIOCRProvider.cs:48-53`, `OpenAITranslationProvider.cs:39-44`.
    **Fix**: Either create HttpClient in the constructor (with the correct BaseUrl) or require all properties to be set before first use with an explicit `Initialize()` method.

11. **`CreateOcrProvider` default case silently falls back to Ollama** — Unknown provider names get silently redirected to Ollama with default settings instead of throwing. This masks configuration errors. File: `ProviderFactory.cs:78-82`.
    **Fix**: Throw `ArgumentException` like `CreateTranslationProvider` does for unknown names.

12. **No test coverage** — Zero .NET unit tests for any Core library code. The `OllamaFuriganaFallbackProvider.ParseRubyOutput` method is 100+ lines of regex parsing that is particularly fragile and untested.

---

### Note: Observations, Risks, and Follow-up Items

13. **`IPlatformServices.CaptureScreenAsync` has no CancellationToken** — Screenshot operations on Linux (`maim`, `import`, `grim`) can hang. File: `IPlatformServices.cs:15`.

14. **`IPlatformServices` returns `System.Drawing.Bitmap`** — This ties the Core library to GDI+ which is deprecated on .NET 6+ for non-Windows. The csproj acknowledges this with `TODO: Replace System.Drawing with SkiaSharp`. File: `OnscreenTranslator.Core/OnscreenTranslator.Core.csproj:9`.

15. **`AppSettings.SettingsPath` uses `AppDomain.CurrentDomain.BaseDirectory`** — When the Core library is loaded by different host apps (WPF, Linux Avalonia, Server), the settings file will be written to the host app's directory, not a shared location. Each host may create its own `settings.json`. File: `AppSettings.cs:143-144`.

16. **`ProtectedStringConverter.PlatformServices` is a static mutable field** — Implicit temporal dependency: if settings are deserialized before this is set, all API keys are read as plaintext/XOR without any warning. File: `AppSettings.cs:56`.

17. **`RemoteServerConfig.RouteOcr`, `RouteTranslation`, `RouteDictionary`** properties appear unused — `ProviderFactory.CreateOcrProvider("remote")` always creates `RemoteOCRProvider` regardless of `RouteOcr`. The routing flags are dead config. File: `AppSettings.cs:62-68`.

18. **Hardcoded entropy in ProtectedStringConverter** — `OnscreenTranslator-ApiKey-Protection-v1` is the same across all installations. A per-user or per-device entropy would improve security. File: `AppSettings.cs:58`.

19. **`RemoteOCRProvider` sends base64-encoded images with no size limit** — Large manga pages can produce multi-MB base64 payloads that could cause OOM or timeout. No compression. File: `RemoteOCRProvider.cs:33-36`.

20. **`DeepLTranslationProvider.TranslateAsync` only maps `ja`→`LanguageCode.Japanese` and `en`→`LanguageCode.EnglishAmerican`** — All other language codes are passed as raw strings to the DeepL SDK. If the SDK expects specific enum values for other languages, this will fail. File: `DeepLTranslationProvider.cs:25-26`.

21. **Singleton `FuriganaServiceManager` is not resettable after dispose** — Calling `Instance` after `Dispose()` creates a new instance, but the old `_disposed` flag is per-instance. This is actually fine, but the pattern is unusual for a singleton.

22. **`IModelListable` is defined in `IOCRProvider.cs`** — It's used by both OCR and translation providers but lives in the OCR interface file. Should be in its own file.

23. **`ProviderFactory.ListModelsForProvider` has redundant code** — After checking `IModelListable`, it also checks `if (provider is OllamaOCRProvider ollamaOcr)` which is unnecessary since `OllamaOCRProvider` already implements `IModelListable`. File: `ProviderFactory.cs:179-181`.

24. **`CreateTranslationProviders` (plural) doesn't create RemoteTranslationProvider** — Only creates Google, DeepL, Ollama, OpenAI, and OpenAI-Compatible. File: `ProviderFactory.cs:78-110`.

---