Now I have a comprehensive picture. Let me compile all findings into a structured review.

## Review

### Correct — What is already good

- **HTTP contract alignment**: The client providers (`RemoteOCRProvider`, `RemoteTranslationProvider`) and the server endpoints are consistent in their JSON contracts. Request/response shapes match perfectly.
  - `RemoteOCRProvider` sends `{ "imageBase64": "...", "model": "auto" }` and reads `{ "text": "..." }` — matches `POST /api/ocr`.
  - `RemoteTranslationProvider` sends `{ "text": "...", "sourceLang": "...", "targetLang": "..." }` and reads `{ "translatedText": "..." }` — matches `POST /api/translate`.

- **Temp file cleanup structure**: The `finally` block in the OCR endpoint ensures cleanup even when the provider throws. The inner `try/catch { }` on `File.Delete` correctly handles the case where the file was never written.

- **Provider factory pattern**: `ProviderFactory` cleanly centralizes provider creation logic and is reused by both the desktop app and server.

- **Bearer token sent from clients**: Both `RemoteOCRProvider` and `RemoteTranslationProvider` correctly set `Authorization: Bearer` headers when an API key is configured.

---

### Findings — Bugs, Security Issues, Design Flaws

#### BLOCKER-1: Temp file leak — `Path.GetTempFileName()` creates a file that is never deleted
**File**: `OnscreenTranslator.Server/Program.cs`, line ~38
**Severity**: **High**

`Path.GetTempFileName()` creates a real 0-byte file on disk (e.g., `/tmp/tmpABCDEF`) and returns its path. The code then appends `.png` to form a *different* path (`/tmp/tmpABCDEF.png`). Only the `.png` file is deleted in the `finally` block. The original file created by `GetTempFileName()` is leaked permanently — one 0-byte orphan per OCR request.

**Fix**: Use a path that doesn't create a file:
```csharp
var tmp = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.png");
```
This generates a unique path without creating a file, and `File.WriteAllBytesAsync` will create it.

---

#### BLOCKER-2: No authentication — server accepts unauthenticated requests
**File**: `OnscreenTranslator.Server/Program.cs` (entire file)
**Severity**: **High**

The client providers (`RemoteOCRProvider`, `RemoteTranslationProvider`) send `Authorization: Bearer <ApiKey>` headers, but the server **never validates** this token. Any network-reachable client can invoke OCR and translation endpoints without credentials. This is a broken auth contract.

**Fix**: Add middleware or a filter that reads `HttpContext.Request.Headers.Authorization`, validates the Bearer token against a configured API key, and returns 401 if invalid:
```csharp
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api") &&
        context.Request.Path != "/api/health")
    {
        var authHeader = context.Request.Headers.Authorization.ToString();
        var expectedKey = settings.RemoteServer.ApiKey; // or a dedicated server key
        if (string.IsNullOrEmpty(expectedKey) ||
            authHeader != $"Bearer {expectedKey}")
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(new { error = "Unauthorized" });
            return;
        }
    }
    await next();
});
```

---

#### BUG-3: `model` parameter parsed but never used — dead code
**File**: `OnscreenTranslator.Server/Program.cs`, line ~40
**Severity**: **Medium**

```csharp
var model = root.TryGetProperty("model", out var m) ? m.GetString() : null;
```

The `model` variable is extracted from the request but never passed to `ProviderFactory.CreateOcrProvider(settings)`. The server always uses whatever model is configured in `AppSettings`, ignoring the client's request. `RemoteOCRProvider` sends `model: "auto"` expecting it to be honored.

**Fix**: Either remove the dead parsing code, or modify `ProviderFactory.CreateOcrProvider` to accept an optional model override.

---

#### BUG-4: Missing JSON properties → HTTP 500 instead of HTTP 400
**File**: `OnscreenTranslator.Server/Program.cs`, lines ~42 and ~67
**Severity**: **Medium**

If a client sends `{}` (missing `imageBase64` or `text`), `root.GetProperty(...)` throws `InvalidOperationException`. The catch block returns **HTTP 500** with the .NET exception message. This should be HTTP 400 (Bad Request) with a descriptive validation error.

**Fix**: Add explicit validation before processing:
```csharp
if (!root.TryGetProperty("imageBase64", out var imgProp) || string.IsNullOrEmpty(imgProp.GetString()))
{
    ctx.Response.StatusCode = 400;
    await ctx.Response.WriteAsJsonAsync(new { error = "Missing required field: imageBase64" }, cancellationToken: ct);
    return;
}
```

---

#### BUG-5: Internal exception messages leak to clients
**File**: `OnscreenTranslator.Server/Program.cs`, lines ~53 and ~75
**Severity**: **Medium**

`ex.Message` is returned directly. This could leak file paths (`/tmp/tmpXXXXXX.png`), connection strings, hostnames, or other internal details to external callers.

**Fix**: Return sanitized error messages:
```csharp
catch (Exception ex)
{
    System.Diagnostics.Debug.WriteLine($"[OCR] Error: {ex}");
    ctx.Response.StatusCode = 500;
    await ctx.Response.WriteAsJsonAsync(new { error = "Internal server error" }, cancellationToken: ct);
}
```

---

#### BUG-6: Provider instances created per-request but never disposed — socket exhaustion
**File**: `OnscreenTranslator.Server/Program.cs`, lines ~44, ~72
**Severity**: **High**

Every request calls `ProviderFactory.CreateOcrProvider(settings)` / `ProviderFactory.CreateTranslationProvider(...)`, which creates a **new** provider instance containing a **new `HttpClient`**. These are never disposed (no `using` on the provider). Under concurrent load, this causes socket exhaustion (`SocketException: Address already in use`).

**Fix**: Use `using` on the provider, or better yet, register providers as singletons or use `IHttpClientFactory`:
```csharp
using var provider = ProviderFactory.CreateOcrProvider(settings);
var text = await provider.RecognizeTextAsync(tmp, ct);
```

---

#### SECURITY-7: No CORS configuration
**File**: `OnscreenTranslator.Server/Program.cs`
**Severity**: **Low-Medium**

No CORS policy is configured. If the server is accessed from any browser context, it accepts all origins by default. While this is a standalone backend, missing CORS can cause unexpected behavior.

**Fix**: Add CORS middleware with an explicit allowed-origins policy, or configure CORS to deny all cross-origin requests if browser access is not intended.

---

#### SECURITY-8: No request body size limit
**File**: `OnscreenTranslator.Server/Program.cs`
**Severity**: **Medium**

The OCR endpoint accepts base64 images with no explicit size limit. ASP.NET Core's default `MaxRequestBodySize` is ~30MB, but this should be explicitly configured. A multi-MB base64 string decoded in memory could cause OOM.

**Fix**: Configure `app.Limits.MaxRequestBodySize` or validate payload size in middleware.

---

#### DESIGN-9: No global exception handler middleware
**File**: `OnscreenTranslator.Server/Program.cs`
**Severity**: **Low-Medium**

Unhandled exceptions outside the endpoint try-catch blocks (e.g., in middleware, during response serialization) would produce raw ASP.NET 500 responses. A global exception handler middleware would provide consistent error responses.

**Fix**: Add `app.UseExceptionHandler("/error")` or a custom exception handling middleware.

---

#### DESIGN-10: Manual JSON parsing instead of model binding
**File**: `OnscreenTranslator.Server/Program.cs`, lines ~36-44, ~63-73
**Severity**: **Low**

Endpoints manually parse `JsonDocument` instead of using typed C# models with `[FromBody]` parameter binding. This is fragile, verbose, and prevents automatic validation and Swagger/OpenAPI generation.

**Fix**: Define request/response DTOs and use model binding:
```csharp
record OcrRequest(string ImageBase64, string? Model);
record TranslateRequest(string Text, string? SourceLang, string? TargetLang);
```

---

#### DESIGN-11: `AppSettings` singleton is mutable — no thread safety
**File**: `OnscreenTranslator.Server/Program.cs`, line ~23; `OnscreenTranslator.Core/Settings/AppSettings.cs`
**Severity**: **Low**

`AppSettings` is registered as a singleton and is a mutable POCO with `Dictionary<string, ProviderConfig>`. If settings are modified at runtime (e.g., via `Save()`), concurrent requests could see inconsistent state.

---

#### DESIGN-12: Self-recursive "remote" provider if server configures `OcrProvider = "remote"`
**File**: `OnscreenTranslator.Server/Program.cs` + `ProviderFactory.cs`
**Severity**: **Low**

If the server's own `AppSettings` has `OcrProvider = "remote"`, then `ProviderFactory.CreateOcrProvider(settings)` would create a `RemoteOCRProvider` pointing back at itself — causing infinite recursion or at minimum unexpected behavior. The server should validate its own configuration or exclude "remote" from its provider options.

---

#### NOTE-13: No tests exist for the server
**Severity**: **Note**

There are zero test files in the entire repository. The server endpoints, provider factory, and error handling paths are all untested.

---

#### NOTE-14: No `launchSettings.json` or `appsettings.json` for the server project
**Severity**: **Note**

The server project has no configuration files for development (port binding, logging, etc.). It relies entirely on defaults (`http://localhost:5000`).

---

### Summary of findings by severity

| Severity | Count | Key Issues |
|----------|-------|------------|
| **High (Blocker)** | 3 | Temp file leak (#1), No authentication (#2), Socket exhaustion (#6) |
| **Medium (Bug)** | 4 | Dead `model` param (#3), 500 for bad input (#4), Exception message leak (#5), No request size limit (#8) |
| **Low-Medium (Security)** | 2 | No CORS (#7), No global exception handler (#9) |
| **Low (Design)** | 3 | Manual JSON parsing (#10), Mutable singleton (#11), Self-recursive remote (#12) |
| **Note** | 2 | No tests (#13), No config files (#14) |

```
acceptance-report
{
  "criteriaSatisfied": [
    {
      "id": "criterion-1",
      "status": "satisfied",
      "evidence": "Returned 14 concrete findings with file paths, line references, severity levels, and code-level fixes across all 6 review focus areas: Program.cs design, security, error handling, provider instantiation, file I/O, and HTTP contract consistency."
    }
  ],
  "changedFiles": [],
  "testsAddedOrUpdated": [],
  "commandsRun": [
    {
      "command": "find + grep + read on all Server and Core provider files",
      "result": "passed",
      "summary": "Inspected Program.cs, ProviderFactory.cs, RemoteOCRProvider.cs, RemoteTranslationProvider.cs, AppSettings.cs, IOCRProvider.cs, ITranslationProvider.cs, OpenAIOCRProvider.cs, OllamaOCRProvider.cs, OpenAITranslationProvider.cs, OllamaTranslationProvider.cs, GoogleTranslationProvider.cs, DeepLTranslationProvider.cs, OpenAIModels.cs, IPlatformServices.cs, .gitignore, OnscreenTranslator.Server.csproj"
    }
  ],
  "validationOutput": [
    "No plan.md or progress.md found in repo (confirmed via find)",
    "No test files found anywhere in repository (confirmed via find)",
    "HTTP contract between RemoteOCRProvider/RemoteTranslationProvider and server endpoints is consistent (verified field-by-field)",
    "Confirmed Path.GetTempFileName() leak: creates file at /tmp/tmpXXXX, appends .png to form new path, only .png deleted in finally"
  ],
  "residualRisks": [
    "3 high-severity issues (temp file leak, no auth, socket exhaustion) should be fixed before production deployment",
    "No tests exist anywhere in the repository — all findings are based on code inspection only",
    "Server is deployed over HTTP by default — API keys and image data travel in plaintext without HTTPS",
    "AppSettings singleton is mutable with no synchronization — runtime settings changes could corrupt state under concurrent requests"
  ],
  "noStagedFiles": true,
  "diffSummary": "No changes made — read-only review of OnscreenTranslator.Server/Program.cs and OnscreenTranslator.Core provider/settings files",
  "reviewFindings": [
    "high: Program.cs:38 - Temp file leak: Path.GetTempFileName() creates file never deleted, only .png variant cleaned up",
    "high: Program.cs:entire - No authentication middleware: Bearer token sent by clients is never validated by server",
    "high: Program.cs:44,72 - Providers created per-request but never disposed (using), causing HttpClient socket exhaustion",
    "medium: Program.cs:40 - model parameter parsed but never used (dead code), client's model request ignored",
    "medium: Program.cs:42,67 - Missing JSON fields cause InvalidOperationException caught as 500 instead of 400",
    "medium: Program.cs:53,75 - ex.Message returned to clients, leaking internal file paths and exception details",
    "medium: Program.cs:no-limit - No request body size limit on base64 image payload, OOM risk",
    "low-medium: Program.cs:no-cors - No CORS policy configured",
    "low-medium: Program.cs:no-middleware - No global exception handler middleware",
    "low: Program.cs:36-73 - Manual JsonDocument parsing instead of typed model binding",
    "low: Program.cs:23 + AppSettings.cs - Mutable singleton AppSettings with no thread-safety",
    "low: Program.cs + ProviderFactory.cs - Self-recursive remote provider if server configures OcrProvider=remote",
    "note: repo-wide - Zero test files exist in entire repository"
  ],
  "manualNotes": "The 3 high-severity blockers (temp file leak, missing auth, socket exhaustion) are straightforward to fix and should be addressed before any production use. The temp file fix is a one-line change. Auth middleware is ~15 lines. Provider disposal is adding 'using' keywords."
}
```