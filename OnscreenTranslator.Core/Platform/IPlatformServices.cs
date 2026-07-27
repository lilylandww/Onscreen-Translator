using System;
using System.Drawing;
using System.Threading.Tasks;

namespace OnscreenTranslator.Core.Platform;

/// <summary>
/// Abstraction over OS-specific operations (screen capture, global hotkeys,
/// overlay windows, data protection, system tray).
/// Each platform (Windows, Linux) provides its own implementation.
/// </summary>
public interface IPlatformServices
{
    /// <summary>
    /// Capture a rectangle of the screen into a Bitmap.
    /// </summary>
    Task<Bitmap> CaptureScreenAsync(Rectangle region);

    /// <summary>
    /// Get the combined bounds of all monitors.
    /// </summary>
    Rectangle GetScreenBounds();

    /// <summary>
    /// Register a global hotkey that works even when the app is not focused.
    /// Returns a disposable that unregisters the hotkey.
    /// </summary>
    IDisposable RegisterGlobalHotkey(KeyModifiers modifiers, VirtualKey key, Action callback);

    /// <summary>
    /// Show an always-on-top overlay window at the given screen region.
    /// Returns a handle/controller that can update content and be dismissed.
    /// </summary>
    IOverlayHandle CreateOverlay(Rectangle region);

    /// <summary>
    /// Encrypt sensitive data (API keys) for local storage.
    /// On Windows this uses DPAPI; on Linux it uses libsecret or a file-based fallback.
    /// </summary>
    byte[] ProtectData(byte[] data, byte[] entropy);

    /// <summary>
    /// Decrypt sensitive data.
    /// </summary>
    byte[] UnprotectData(byte[] data, byte[] entropy);

    /// <summary>
    /// Show a system-tray icon with a context menu.
    /// Returns a disposable that removes the tray icon.
    /// </summary>
    IDisposable ShowSystemTrayIcon(string iconPath, SystemTrayMenu menu);

    /// <summary>
    /// Restore the main window from tray (bring to front, show).
    /// </summary>
    void RestoreMainWindow();
}

public interface IOverlayHandle : IDisposable
{
    /// <summary>
    /// Update the overlay content (text, furigana, translation).
    /// </summary>
    void UpdateContent(OverlayState state);

    /// <summary>
    /// Move and resize the overlay.
    /// </summary>
    void SetBounds(Rectangle bounds);

    /// <summary>
    /// Show or hide the overlay.
    /// </summary>
    void SetVisible(bool visible);
}

[Flags]
public enum KeyModifiers
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Super = 8,  // Windows key / Meta key
}

public enum VirtualKey
{
    None = 0,
    Escape, F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,
    A, B, C, D, E, F, G, H, I, J, K, L, M,
    N, O, P, Q, R, S, T, U, V, W, X, Y, Z,
    D0, D1, D2, D3, D4, D5, D6, D7, D8, D9,
    Left, Right, Up, Down,
    Tab, Enter, Space, Backspace, Delete,
    LeftControl, RightControl, LeftAlt, RightAlt, LeftShift, RightShift,
    PrintScreen, ScrollLock, Pause,
}

public record SystemTrayMenu
{
    public SystemTrayMenuItem[] Items { get; init; } = [];
}

public record SystemTrayMenuItem
{
    public string Label { get; init; } = "";
    public Action? Action { get; init; }
    public bool IsSeparator { get; init; }
    public bool IsChecked { get; init; }
}
