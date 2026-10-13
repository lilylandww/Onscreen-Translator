using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace WpfAppTest;

/// <summary>
/// Registers process-wide hotkeys against the main window's HWND and dispatches
/// WM_HOTKEY messages. Works regardless of which app has focus, so the overlay
/// can be controlled while the user is typing/scrolling elsewhere.
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    public const int ToggleOverlayId = 0x5001;
    public const int DrawRegionId = 0x5002;

    /// <summary>Raised with the hotkey id on the UI thread when a registered hotkey fires.</summary>
    public event Action<int>? HotkeyPressed;

    private readonly IntPtr _hwnd;
    private readonly HwndSource? _source;
    private bool _toggleRegistered;
    private bool _drawRegistered;
    private bool _disposed;

    public HotkeyManager(Window window)
    {
        _hwnd = new WindowInteropHelper(window).Handle;
        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(WndProc);
    }

    /// <summary>
    /// (Re-)registers both hotkeys. Idempotent: unregisters any previous ones first.
    /// Unparseable or conflicting hotkeys are skipped with a debug log.
    /// </summary>
    public void Register(string? toggleHotkey, string? drawHotkey)
    {
        if (_disposed) return;

        UnregisterAll();

        if (TryParse(toggleHotkey, out uint toggleMods, out uint toggleVk))
        {
            _toggleRegistered = OSInterop.RegisterHotKey(
                _hwnd, ToggleOverlayId, toggleMods | OSInterop.MOD_NOREPEAT, toggleVk);
            if (!_toggleRegistered)
                Debug.WriteLine($"[HotkeyManager] Failed to register toggle hotkey '{toggleHotkey}' (conflict?).");
        }
        else if (!string.IsNullOrWhiteSpace(toggleHotkey))
        {
            Debug.WriteLine($"[HotkeyManager] Could not parse toggle hotkey '{toggleHotkey}'.");
        }

        if (TryParse(drawHotkey, out uint drawMods, out uint drawVk))
        {
            _drawRegistered = OSInterop.RegisterHotKey(
                _hwnd, DrawRegionId, drawMods | OSInterop.MOD_NOREPEAT, drawVk);
            if (!_drawRegistered)
                Debug.WriteLine($"[HotkeyManager] Failed to register draw hotkey '{drawHotkey}' (conflict?).");
        }
        else if (!string.IsNullOrWhiteSpace(drawHotkey))
        {
            Debug.WriteLine($"[HotkeyManager] Could not parse draw hotkey '{drawHotkey}'.");
        }
    }

    public void UnregisterAll()
    {
        if (_toggleRegistered)
        {
            OSInterop.UnregisterHotKey(_hwnd, ToggleOverlayId);
            _toggleRegistered = false;
        }
        if (_drawRegistered)
        {
            OSInterop.UnregisterHotKey(_hwnd, DrawRegionId);
            _drawRegistered = false;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == OSInterop.WM_HOTKEY && wParam.ToInt32() is ToggleOverlayId or DrawRegionId)
        {
            HotkeyPressed?.Invoke(wParam.ToInt32());
            handled = true;
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// Parses a hotkey string like "Ctrl+Alt+O" into RegisterHotKey modifiers + virtual key.
    /// Supported modifiers: Ctrl, Control, Alt, Shift, Win. Key may be a letter,
    /// digit, F1-F24, or a <see cref="Key"/> enum name.
    /// </summary>
    public static bool TryParse(string? text, out uint modifiers, out uint virtualKey)
    {
        modifiers = 0;
        virtualKey = 0;

        if (string.IsNullOrWhiteSpace(text))
            return false;

        string[] parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
            return false;

        for (int i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    modifiers |= OSInterop.MOD_CONTROL;
                    break;
                case "alt":
                    modifiers |= OSInterop.MOD_ALT;
                    break;
                case "shift":
                    modifiers |= OSInterop.MOD_SHIFT;
                    break;
                case "win":
                case "windows":
                    modifiers |= OSInterop.MOD_WIN;
                    break;
                default:
                    return false;
            }
        }

        string keyPart = parts[^1];
        if (keyPart.Length == 1 && char.IsLetterOrDigit(keyPart[0]))
        {
            virtualKey = char.ToUpperInvariant(keyPart[0]);
            return true;
        }

        if (keyPart.Length >= 2 && (keyPart[0] is 'F' or 'f')
            && int.TryParse(keyPart[1..], out int fNum) && fNum is >= 1 and <= 24)
        {
            virtualKey = (uint)(0x70 + (fNum - 1)); // VK_F1 = 0x70
            return true;
        }

        if (Enum.TryParse<Key>(keyPart, ignoreCase: true, out Key key) && key != Key.None)
        {
            virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
            return virtualKey != 0;
        }

        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        UnregisterAll();
        _source?.RemoveHook(WndProc);
    }
}
