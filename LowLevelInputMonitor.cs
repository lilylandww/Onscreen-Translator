using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WpfAppTest;

/// <summary>
/// System-wide low-level mouse + keyboard hook used by overlay mode for two
/// things: (1) pointer-position tracking so click-through can be toggled when
/// the cursor is over the translation card, and (2) input-activity notifications
/// that drive screen-change invalidation.
/// Hooks run on the installing (UI) thread's message loop; callbacks must stay
/// fast or Windows silently drops the hook (LowLevelHooksTimeout).
/// </summary>
public sealed class LowLevelInputMonitor : IDisposable
{
    /// <summary>Raised on every pointer move (UI thread). Point is physical screen px.</summary>
    public event Action<System.Drawing.Point>? PointerMoved;

    /// <summary>Raised on mouse button presses (UI thread). Point is physical screen px.</summary>
    public event Action<System.Drawing.Point>? PointerButton;

    /// <summary>Raised on mouse wheel events (UI thread). Point is physical screen px.</summary>
    public event Action<System.Drawing.Point>? Wheel;

    /// <summary>Raised on non-modifier key down events (UI thread).</summary>
    public event Action? KeyDownActivity;

    private IntPtr _mouseHook;
    private IntPtr _keyboardHook;
    private OSInterop.HookProc? _mouseProc;
    private OSInterop.HookProc? _keyProc;
    private bool _started;
    private bool _disposed;

    public void Start()
    {
        if (_started || _disposed) return;

        _mouseProc = MouseHookCallback;
        _keyProc = KeyboardHookCallback;

        using Process current = Process.GetCurrentProcess();
        using ProcessModule module = current.MainModule ?? throw new InvalidOperationException("No main module");
        IntPtr hModule = OSInterop.LoadLibrary(module.ModuleName);

        _mouseHook = OSInterop.SetWindowsHookEx(OSInterop.WH_MOUSE_LL, _mouseProc, hModule, 0);
        _keyboardHook = OSInterop.SetWindowsHookEx(OSInterop.WH_KEYBOARD_LL, _keyProc, hModule, 0);

        if (_mouseHook == IntPtr.Zero || _keyboardHook == IntPtr.Zero)
            Debug.WriteLine("[InputMonitor] Failed to install one or more low-level hooks.");

        _started = true;
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var info = Marshal.PtrToStructure<OSInterop.MSLLHOOKSTRUCT>(lParam);
            var point = new System.Drawing.Point(info.Pt.X, info.Pt.Y);
            int msg = wParam.ToInt32();

            switch (msg)
            {
                case OSInterop.WM_MOUSEMOVE:
                    PointerMoved?.Invoke(point);
                    break;
                case OSInterop.WM_LBUTTONDOWN:
                case OSInterop.WM_RBUTTONDOWN:
                case OSInterop.WM_MBUTTONDOWN:
                case OSInterop.WM_XBUTTONDOWN:
                    PointerButton?.Invoke(point);
                    break;
                case OSInterop.WM_MOUSEWHEEL:
                case OSInterop.WM_MOUSEHWHEEL:
                    Wheel?.Invoke(point);
                    break;
            }
        }
        return OSInterop.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            // Only key-down transitions (WM_KEYDOWN / WM_SYSKEYDOWN).
            int msg = wParam.ToInt32();
            if (msg == OSInterop.WM_KEYDOWN || msg == 0x0104 /* WM_SYSKEYDOWN */)
            {
                int vk = Marshal.ReadInt32(lParam);
                if (!IsModifierOrToggle(vk))
                    KeyDownActivity?.Invoke();
            }
        }
        return OSInterop.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    private static bool IsModifierOrToggle(int vk) => vk is
        0x10 or 0x11 or 0x12 or            // Shift / Ctrl / Alt
        0xA0 or 0xA1 or 0xA2 or 0xA3 or    // L/R Shift, L/R Ctrl
        0xA4 or 0xA5 or                    // L/R Alt (menu)
        0x5B or 0x5C or                    // L/R Win
        0x14 or 0x90 or 0x91 or            // CapsLock / NumLock / ScrollLock
        0x2C;                              // PrintScreen

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_mouseHook != IntPtr.Zero)
        {
            OSInterop.UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }
        if (_keyboardHook != IntPtr.Zero)
        {
            OSInterop.UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
        }
        // Keep delegates alive until hooks are gone (assigned above).
        _mouseProc = null;
        _keyProc = null;
    }
}
