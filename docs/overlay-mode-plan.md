# Overlay Mode — Design & Implementation Plan

## UX concept

A persistent, always-on-top, mostly **click-through** layer. The user keeps using
their app; the translation is pinned **over the drawn region**, and the moment
scrolling/interaction changes that region it fades out and goes quiet until the
user draws again. A tiny chip explains how to get it back.

## User flow

1. **Enable Overlay Mode** — toolbar toggle (next to `LiveModeToggleButton`) or
   global hotkey `Ctrl+Alt+O` (persisted in `AppSettings`). Window becomes
   `Topmost`, non-activating, click-through; screen-freeze is forced off
   (Live-style capture is implied).
2. **Draw region** — `Ctrl+Alt+T` (or click the chip). Same selection UX as today
   (dim + crosshair); the window is temporarily interactive. On `MouseUp`:
   existing capture → OCR → furigana → translate pipeline; the translation card
   appears **over the region**; window returns to click-through.
3. **Use the underlying app** — clicks/scrolls pass through. Scrolling/clicking/
   typing that changes the region invalidates the translation: card fades out
   (~200 ms) and a small chip appears at the region's corner:
   `Screen changed · Ctrl+Alt+T to redraw` (click chip or hotkey to redraw).
4. **Redraw** — chip / `Ctrl+Alt+T` re-enters selection. Translation stays off
   until a new area is drawn (per requirement).
5. **Dismiss** — chip's `×`, or Esc when the card is focused. `Ctrl+Alt+O` exits
   Overlay Mode entirely.

## Key mechanics

### Hybrid hit-test (seamless interaction)

One `HwndSource` hook on `MainWindow` — `WM_NCHITTEST` returns
`HTTRANSPARENT` outside interactive elements, so clicks fall through to the app
below; over the translation card/chip/selection UI it returns `HTCLIENT`
(select/copy, `OverlayContextMenu`, drag grip, edit). `WS_EX_NOACTIVATE` keeps
focus in the user's app so they can keep typing; a card click may activate
normally (user intent). `Window_Deactivated` must **not** clear the overlay while
Overlay Mode is on — this is the current behavior that kills the feature.

### Stale detection ("scroll/interact toggles it off")

Primary = input-driven invalidation via `WH_MOUSE_LL` hook (wheel/click/drag) +
key events, debounced ~150 ms — snappy and needs no capture. Refinement =
pixel-confirm: capture the region and diff vs the reference frame taken right
after translation (downsampled ~64×64, mean-abs-diff vs sensitivity threshold)
so harmless clicks (cursor blink, focus-only) don't dismiss. Pixel-confirm
requires capturing *without our own card covering the region* → try
`SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)`; if GDI `CopyFromScreen`
still includes the window, fall back to input-only invalidation. Input events on
the card itself never invalidate.

### Card over region

Positioned in DIPs from the existing `scaledRegion` math (`Canvas_MouseUp`),
width = region width, height grows beyond region if needed, existing
`OverlayBackground` (AntiqueWhite 0.8) cover, drop shadow, drag grip. Keeps
`OverlayContextMenu`, F-key view cycle, double-click edit, furigana view.

## New/changed pieces

| Piece | What |
|---|---|
| `HotkeyManager.cs` (new) | `RegisterHotKey`/`UnregisterHotKey` P/Invoke + message hook; `Ctrl+Alt+O` toggle, `Ctrl+Alt+T` draw (configurable) |
| `OverlayManager.cs` (new) | Mode state, `Topmost` + `WS_EX_NOACTIVATE\|WS_EX_TOOLWINDOW\|WS_EX_LAYERED` via `Get/SetWindowLongPtr`, `WM_NCHITTEST` hook, gate `Window_Deactivated` |
| `RegionChangeWatcher.cs` (new) | `WH_MOUSE_LL`/`WH_KEYBOARD_LL` hooks, debounce, optional pixel-diff confirm, fade-out + chip |
| `MainWindow.xaml(.cs)` | Card chrome (Border/grip/chip) around `translatedTextBlock`/`rubyTextBlock`; overlay-mode placement; reuse live-mode hide-chrome capture; gate deactivation clearing |
| `AppSettings.cs` | `OverlayMode`, `OverlayHotkeyToggle/Draw`, `OverlayInvalidateOnInput`, `OverlayPixelConfirm`, `OverlayChangeSensitivity` (next to `LiveMode`) |
| `SettingsWindow` | New "Overlay" section; tray menu: "Toggle Overlay Mode" / "Draw Region" |
| `OSInterop.cs`/`NativeMethods.cs` | `SetWindowLongPtr`, `GetWindowLongPtr`, `RegisterHotKey`, `SetWindowDisplayAffinity`, `WM_NCHITTEST`, `HTTRANSPARENT`, `WH_MOUSE_LL` |

## Edge cases

- Multi-monitor/DPI: reuse `TransformToDevice`, `GetAbsolutePosition`,
  `DisplayInfo` math; LL hook points are screen coords → compare to region rect.
- Editing the translation: card click activates the window (intentional);
  `FinishEditing` restores click-through.
- Exclusive-fullscreen games won't show layered windows (WPF/DWM limitation) —
  borderless windowed works; document in README.

## Verification

- `dotnet build WpfAppTest.sln` (0 warnings/0 errors),
  `python3 -m pytest furigana-service/tests/ -v` (untouched).
- Manual Windows checklist: draw → translation over region; click/scroll in
  browser below → chip appears, card gone; click through everywhere;
  copy/context menu on card; hotkeys in/out of focus; multi-monitor; DPI
  scaling; deactivate window → overlay persists; Esc/× dismissal.

## Steps

1. Branch `agent/overlay-mode`; write plan to `docs/overlay-mode-plan.md`.
2. `HotkeyManager` → 3. `OverlayManager` (styles + hit-test + deactivation gate)
→ 4. card chrome/placement → 5. `RegionChangeWatcher` → 6. settings/tray →
7. polish (fade, chip, Esc, monitors/DPI) → 8. build + checklist → commit
`feature: overlay mode with click-through translation card and screen-change
invalidation` + session summary in `docs/agent-sessions/`.
