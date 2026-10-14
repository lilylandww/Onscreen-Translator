using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;

namespace WpfAppTest;

/// <summary>
/// Owns the Win32 window-style changes that turn the main window into an
/// overlay: always-on-top, out of the taskbar/alt-tab (tool window), excluded
/// from screen capture (WDA_EXCLUDEFROMCAPTURE), and a click-through /
/// non-activating state that can be toggled per-pointer-position so the
/// translation card stays interactive while everything else passes through.
///
/// Note on the click-through mechanism: the obvious per-element alternative —
/// returning HTTRANSPARENT from WM_NCHITTEST — only passes hits to
/// "underlying windows in the same thread" (WM_NCHITTEST docs), so it cannot
/// forward clicks to other processes, which is the entire point of this
/// feature. WS_EX_TRANSPARENT (layered + extended style) is the supported
/// cross-process mechanism; the low-level mouse hook flips it so the card
/// region stays interactive (see LowLevelInputMonitor for the watchdog that
/// keeps the hook alive).
/// </summary>
public sealed class OverlayModeManager : IDisposable
{
    private readonly Window _window;
    private readonly IntPtr _hwnd;
    private bool _enabled;
    // Cached extended-style bits (window starts without them).
    private bool _clickThrough;
    private bool _noActivate;
    private bool _disposed;

    public bool IsEnabled => _enabled;
    public bool IsClickThrough => _clickThrough;
    public bool IsNoActivate => _noActivate;

    public OverlayModeManager(Window window)
    {
        _window = window;
        _hwnd = new WindowInteropHelper(window).Handle;
    }

    public void Enable()
    {
        if (_enabled || _disposed) return;
        _enabled = true;

        _window.Topmost = true;
        _window.ShowInTaskbar = false;

        // Tool window: keeps the fullscreen overlay out of alt-tab / taskbar.
        AddExStyle(OSInterop.WS_EX_TOOLWINDOW);

        // Best effort: exclude the overlay from screen capture (Win10 2004+).
        // RegionChangeWatcher calibrates whether GDI capture honors it.
        bool affinityOk = OSInterop.SetWindowDisplayAffinity(_hwnd, OSInterop.WDA_EXCLUDEFROMCAPTURE);
        if (!affinityOk)
            Debug.WriteLine("[OverlayModeManager] SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE) unsupported; using hide-to-capture fallback.");

        SetClickThrough(true);
        SetNoActivate(true);
    }

    public void Disable()
    {
        if (!_enabled || _disposed) return;
        _enabled = false;

        SetClickThrough(false);
        SetNoActivate(false);
        RemoveExStyle(OSInterop.WS_EX_TOOLWINDOW);
        OSInterop.SetWindowDisplayAffinity(_hwnd, 0); // WDA_NONE

        _window.Topmost = false;
        _window.ShowInTaskbar = _window.WindowState != WindowState.Minimized;
    }

    /// <summary>
    /// Toggles WS_EX_TRANSPARENT: when set, mouse input passes through the
    /// whole window to the app below. The caller updates this based on whether
    /// the pointer is over an interactive overlay element (card, chip, drag).
    /// </summary>
    public void SetClickThrough(bool clickThrough)
    {
        if (_disposed || _clickThrough == clickThrough) return;
        _clickThrough = clickThrough;
        ToggleExStyle(OSInterop.WS_EX_TRANSPARENT, clickThrough);
    }

    /// <summary>
    /// Toggles WS_EX_NOACTIVATE: when set, clicking the overlay never steals
    /// keyboard focus from the app the user is working in.
    /// </summary>
    public void SetNoActivate(bool noActivate)
    {
        if (_disposed || _noActivate == noActivate) return;
        _noActivate = noActivate;
        ToggleExStyle(OSInterop.WS_EX_NOACTIVATE, noActivate);
    }

    private void ToggleExStyle(int style, bool on)
    {
        IntPtr current = OSInterop.GetWindowLongPtr(_hwnd, OSInterop.GWL_EXSTYLE);
        long value = current.ToInt64();
        long bit = style;
        long updated = on ? value | bit : value & ~bit;
        if (updated == value) return;

        OSInterop.SetWindowLongPtr(_hwnd, OSInterop.GWL_EXSTYLE, new IntPtr(updated));
    }

    private void AddExStyle(int style) => ToggleExStyle(style, true);

    private void RemoveExStyle(int style) => ToggleExStyle(style, false);

    public void Dispose()
    {
        if (_disposed) return;
        if (_enabled)
        {
            // Never leave a window permanently click-through / topmost on teardown.
            ToggleExStyle(OSInterop.WS_EX_TRANSPARENT, false);
            ToggleExStyle(OSInterop.WS_EX_NOACTIVATE, false);
            RemoveExStyle(OSInterop.WS_EX_TOOLWINDOW);
            _enabled = false;
        }
        _disposed = true;
    }
}
