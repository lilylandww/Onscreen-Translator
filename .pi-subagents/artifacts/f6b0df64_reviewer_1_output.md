Now I have all the evidence I need. Let me compile the comprehensive review.

## Review

### 1. Program.cs — Startup Flow

**Correct:**
- Clean startup: creates `LinuxPlatformServices`, sets `ProtectedStringConverter.PlatformServices`, loads settings, builds Avalonia, starts with classic desktop lifetime.
- Design-time safety: not an issue here since this is only the entry point.

**Issues:**
- **Note** `Program.cs:1` — No `using` needed, but the class lacks `internal`/`public` modifier. The `class Program` defaults to `internal`. This is fine but inconsistent with other files that use `public`.

---

### 2. App.axaml / App.cs — Avalonia Application Lifecycle

**Correct:**
- XAML is minimal and clean: FluentTheme applied, correct namespace.
- `OnFrameworkInitializationCompleted` properly checks for `IClassicDesktopStyleApplicationLifetime` before creating `MainWindow`.
- Design-time constructor provided (`App()` default) with reasonable defaults.

**Issues:**
- **Note** `App.cs:19` — Design-time constructor creates `new LinuxPlatformServices()`. This is functional but means the XAML designer will try to instantiate real platform services. Acceptable for now, but a `NullPlatformServices` stub would be safer for design-time.

---

### 3. MainWindow.axaml / MainWindow.axaml.cs — UI, Capture Flow, Remote Config

**Correct:**
- Remote config panel visibility toggling via `IsRemoteSelected()` is correct: fires on both OCR and Translation combo changes.
- `OnOcrProviderChanged` / `OnTranslationProviderChanged` correctly map display names to provider keys.
- `SaveSettingsFromUi()` properly populates `_settings.RemoteServer` from UI controls.

**Issues:**

- **BLOCKER** `MainWindow.axaml.cs:57` — `bitmap.Save(tmp, System.Drawing.Imaging.ImageFormat.Png)`: This uses `System.Drawing.Common` APIs. On .NET 8 Linux without the `System.Drawing.EnableUnixSupport` runtime switch, this will throw `PlatformNotSupportedException`. This is the same blocker as in `LinuxPlatformServices`.

- **Bug** `MainWindow.axaml.cs:50` — `var tmp = System.IO.Path.GetTempFileName() + ".png";` — `Path.GetTempFileName()` creates a real file (e.g., `/tmp/tmpXXXX.tmp`). Appending `.png` creates a *different* path (`/tmp/tmpXXXX.tmp.png`). The original `/tmp/tmpXXXX.tmp` file is **never deleted**. The `finally` block only deletes the `.png` version. This **leaks temp files** on every capture.

- **Bug** `MainWindow.axaml.cs:55` — `this.Hide()` is called, but there is no `await Task.Delay(...)` or yield to allow the Avalonia compositor to actually render the window as hidden before the screenshot is taken. On fast systems the window may still be visible in the captured image. A brief delay (e.g., `await Task.Delay(200)`) is needed between `Hide()` and the capture.

- **Note** `MainWindow.axaml.cs:43-53` — The capture flow captures the full screen (`_platform.GetScreenBounds()`) as a placeholder, but the TODO says "show a region selection overlay." This means the overlay feature (the core UX of the app) is non-functional. Users capture their entire screen, not a region.

- **Note** `MainWindow.axaml.cs:75-82` — `OnSettingsClicked` saves settings but the TODO for "open a settings dialog" is unimplemented. The button saves silently with no user feedback.

- **Note** `MainWindow.axaml.cs` — Remote server config (URL, API key, checkboxes) is only persisted when the user clicks "Settings." Changing the combo box immediately updates `_settings.OcrProvider` / `_settings.TranslationProvider`, but the remote server fields (URL, key) are not written to `_settings` until "Settings" is clicked. This is inconsistent UX — the remote fields appear but won't take effect until a separate save action.

---

### 4. LinuxPlatformServices.cs — Screenshots, Wayland, XorCrypt, RunCommand

**Correct:**
- `IsWayland()` detection: checks both `WAYLAND_DISPLAY` and `XDG_SESSION_TYPE` — covers the common cases.
- `HasTool()`: uses `which` to detect tool availability — correct approach.
- Screenshot tool fallback chain (X11): `maim` → `import` (ImageMagick) — reasonable.
- `XorCrypt`: symmetric, functionally correct (though not secure).
- `GetScreenBounds()`: parses `xrandr` output correctly, with sensible fallback.

**Issues:**

- **BLOCKER** `LinuxPlatformServices.cs:22-62` — Entire `CaptureScreenAsync` uses `System.Drawing.Bitmap` and `System.Drawing.Imaging` (line 4: `using System.Drawing.Imaging`). On .NET 8 Linux without `System.Drawing.EnableUnixSupport`, all these APIs will throw `PlatformNotSupportedException`. The built binary's `runtimeconfig.json` does **not** set this switch. This makes the entire screenshot path non-functional on Linux.

- **Bug** `LinuxPlatformServices.cs:44-47` — **Dead code**: The first `var args = ...` block (computing Wayland/X11 args) is computed but **never used**. The actual arguments are in `var toolArgs` (lines 49-56). This is leftover from a refactoring and is confusing.

- **Bug** `LinuxPlatformServices.cs:26` — `var tmp = Path.GetTempFileName() + ".png";` — Same temp file leak as in `MainWindow.axaml.cs`. `GetTempFileName()` creates a file at the base path; appending `.png` means the original file is orphaned and never cleaned up. Over many captures, this leaks files in `/tmp`.

- **Bug** `LinuxPlatformServices.cs:32-33` — **Wayland: `grim` fallback missing**: On Wayland, the code sets `tool = "grim"` but never checks if `grim` is installed (unlike X11 where `HasTool("maim")` is checked). If `grim` is not installed, `RunCommand("grim", ...)` will throw. A `HasTool("grim")` check with fallback instructions is needed.

- **Note** `LinuxPlatformServices.cs:49-56` — `grim` geometry format: `-g "X,Y WxH"` is correct for grim. However, the code generates the arguments using the `IsWayland()` check on lines 43/49 but the initial `args` variable (line 43) uses a different grim format (`--geometry=...`) which is **wrong** for grim (grim doesn't have `--geometry`). Fortunately, `args` is dead code so this doesn't cause a runtime error, but it reveals the original intent was wrong.

- **Note** `LinuxPlatformServices.cs:74-89` — `GetScreenBounds()` uses `xrandr`. On pure Wayland sessions (especially wlroots-based), `xrandr` may not be available or may not report the correct resolution. Consider falling back to `wlr-randr` or reading from the Wayland compositor. The 1920x1080 fallback may be incorrect for HiDPI or ultrawide displays.

- **Note** `LinuxPlatformServices.cs:120-134` — `RunCommandAndRead` wraps command in `/bin/bash -c "..."` but never reads stderr. If the child process writes enough to stderr to fill the OS pipe buffer, the process will block and `WaitForExit(5000)` will time out, but the process may not be properly cleaned up (the `using` helps, but the process is still hanging). Also, the bash quoting (`$"\"{command}\""`) is vulnerable to injection if `command` contains double quotes or shell metacharacters. For the current call site (`xrandr`), this is fine, but it's fragile.

- **Note** `LinuxPlatformServices.cs:117-134` — `RunCommand` does not check the exit code. If the screenshot tool fails with a non-zero exit, the code only checks `File.Exists(tmp)`. A non-zero exit with no output file produces a generic "Screenshot tool failed" message with no stderr info.

---

### 5. GtkOverlayHandle.cs — Stub Implementation

**Assessment: Pure stub, non-functional.**

Every method (`UpdateContent`, `SetBounds`, `SetVisible`, `Dispose`) is a TODO with no implementation. The overlay — which is the core visual feature of the translator (showing translations on-screen) — is completely non-functional.

**Missing for real implementation:**
- An Avalonia `Popup`, `TopLevel`, or custom `Window` with `TransparencyLevelHint=" acrylic"` or `"blur"` and `ExtendClientAreaToDecorationsHint="True"` to create a transparent, always-on-top overlay.
- Text rendering with support for furigana (ruby text) — the `RubyTextBlock` control exists in the root project's `Controls/` directory but isn't wired up.
- Proper `Dispose` to close/destroy the overlay window.
- Event loop integration: the overlay needs to be created and managed on the Avalonia UI thread.

This is the single biggest feature gap in the Linux port.

---

### 6. LinuxHotkeyRegistration.cs — Polling Approach

**Assessment: Complete no-op stub.**

The `PollLoop` method runs a `Task.Delay(100)` loop that never reads any key state and never invokes the callback. It will spin-uselessly until disposed. The `Dispose` method correctly cancels the token.

**Viability:** The polling approach itself (if implemented) would work for detecting key presses via `XQueryKeymap` (X11) or `/dev/input` (both). However:
- Polling at 100ms introduces up to 100ms latency for hotkey response.
- On X11, `XGrabKey` is the proper approach (kernel-level key grab, no polling needed).
- On Wayland, global key grabs are intentionally restricted; the recommended approach is to use a compositor-specific protocol or an external tool like `sxhkd`.

**Suggested concrete path:**
- X11: Use `Xlib.XGrabKey` / `XQueryKeymap` via P/Invoke.
- Wayland: Accept `sxhkd` as an external dependency, or use D-Bus to communicate with the compositor.
- Both: Shell out to `xdotool` for key state detection as a quick fallback.

---

### 7. LinuxTrayIcon.cs — Stub Completeness

**Assessment: Pure stub, non-functional.**

Constructor stores `iconPath` and `menu` but performs no actions. `Dispose` is empty. No system tray icon will appear.

**For real implementation:**
- Use `StatusNotifierItem` D-Bus protocol (KDE/GNOME both support this).
- Alternatively, use `libappindicator` bindings (available via `Tmds.DBus.Protocol` which is already in the deps — it's a transitive dependency of Avalonia.FreeDesktop).
- Need to handle icon from file path or embedded resource.
- Need to wire up menu item click callbacks.

---

### Cross-Cutting Issues

- **`System.Drawing.Common` on .NET 8 Linux** — This is the **critical blocker**. The `runtimeconfig.json` does not include `"System.Drawing.EnableUnixSupport": true`. Without this, all `System.Drawing` API calls (bitmap creation, saving, manipulation) will throw `PlatformNotSupportedException`. This breaks:
  - `CaptureScreenAsync` (LinuxPlatformServices.cs)
  - `OnCaptureClicked` in MainWindow (bitmap.Save)
  - All provider code that uses `System.Drawing.Bitmap` (OllamaOCRProvider, OpenAIOCRProvider)
  
  **Fix:** Add to csproj or create/edit `OnscreenTranslator.Linux.runtimeconfig.json`:
  ```json
  "configProperties": {
    "System.Drawing.EnableUnixSupport": true
  }
  ```
  Or better: replace `System.Drawing.Common` with SkiaSharp or ImageSharp (noted as TODO in Core csproj).

- **No tests** — Zero unit or integration tests exist for the Linux project. The capture flow, provider wiring, settings persistence, and Wayland detection are all untested.

- **No `.gitignore` for temp files** — Not critical, but worth noting.

```
## Review
- Correct: Program.cs startup flow is clean and correctly wires PlatformServices before settings load. App.cs lifecycle management and design-time constructor are well-structured. Remote config panel visibility logic in MainWindow is correct. IsWayland() detection covers both env vars. X11 screenshot tool fallback chain (maim→import) is reasonable. Settings JSON persistence with ProtectedStringConverter is sound.
- Fixed: (none — review-only)
- Blocker: LinuxPlatformServices.cs + MainWindow.axaml.cs — System.Drawing.Common on .NET 8 Linux throws PlatformNotSupportedException without "System.Drawing.EnableUnixSupport" runtime switch. The built runtimeconfig.json does not include this switch. Every bitmap operation (capture, save, OCR preprocessing) will crash at runtime. Fix: add configProperties to runtimeconfig.json or csproj, or replace System.Drawing with SkiaSharp.
- Note: LinuxPlatformServices.cs:44-47 — Dead `args` variable never used (leftover from refactoring).
- Note: LinuxPlatformServices.cs:26 and MainWindow.axaml.cs:50 — Path.GetTempFileName() + ".png" leaks the original temp file created by GetTempFileName(). Every capture leaks one file in /tmp.
- Note: LinuxPlatformServices.cs:32 — Wayland path never checks if grim is installed (unlike X11 which checks HasTool("maim")). Will throw if grim is missing.
- Note: MainWindow.axaml.cs:55 — No delay between this.Hide() and screenshot capture. Window may still be visible in captured image.
- Note: GtkOverlayHandle.cs — All methods are empty stubs. The core overlay feature (showing translations on-screen) is completely non-functional.
- Note: LinuxHotkeyRegistration.cs — PollLoop is a no-op busy-wait that never reads key state. Global hotkeys are non-functional.
- Note: LinuxTrayIcon.cs — Pure stub. System tray icon will not appear.
- Note: RunCommandAndRead (LinuxPlatformServices.cs:120-134) has potential deadlock: redirects stderr but never reads it, then calls WaitForExit(5000). If stderr buffer fills, process blocks.
- Note: MainWindow.axaml.cs — Remote server config fields (URL, key) are only saved to _settings when "Settings" is clicked, but combo box changes apply immediately. Inconsistent UX.
- Note: No unit or integration tests exist for the Linux project.
```