using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace WpfAppTest
{
    public class SystemTrayIcon : IDisposable
    {
        private readonly IntPtr _hWnd;
        private readonly HwndSource _hwndSource;
        private readonly IntPtr _hIcon;
        private readonly Action? _onRestore;
        private readonly Action? _onSettings;
        private readonly Action? _onExit;
        private readonly uint _taskbarCreatedMessage;
        private bool _disposed;
        private bool _added;

        private const string WindowClass = "WpfAppTestTrayIcon";
        private const int WM_USER = 0x0400;
        private const int WM_TRAYICON = WM_USER + 0;
        private const int WM_LBUTTONUP = 0x0202;
        private const int WM_RBUTTONUP = 0x0205;
        private const int WM_COMMAND = 0x0111;

        private const uint NIM_ADD = 0x00000000;
        private const uint NIM_MODIFY = 0x00000001;
        private const uint NIM_DELETE = 0x00000002;

        private const uint NIF_MESSAGE = 0x00000001;
        private const uint NIF_ICON = 0x00000002;
        private const uint NIF_TIP = 0x00000004;

        private const int MFT_STRING = 0x00000000;
        private const int MFT_SEPARATOR = 0x00000800;
        private const int MF_BYPOSITION = 0x00000400;
        private const int MF_POPUP = 0x00000010;
        private const uint TPM_LEFTALIGN = 0x0000;
        private const uint TPM_RETURNCMD = 0x0100;
        private const int HWND_MESSAGE = -3;

        private const int IDM_RESTORE = 1000;
        private const int IDM_SETTINGS = 1001;
        private const int IDM_EXIT = 1002;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NOTIFYICONDATA
        {
            public uint cbSize;
            public IntPtr hWnd;
            public uint uID;
            public uint uFlags;
            public uint uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szTip;
            public uint dwState;
            public uint dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string szInfo;
            public uint uVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string szInfoTitle;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

        [DllImport("user32.dll")]
        private static extern IntPtr CreatePopupMenu();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, UIntPtr uIDNewItem, string lpNewItem);

        [DllImport("user32.dll")]
        private static extern int TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y, int nReserved, IntPtr hWnd, IntPtr prcRect);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool DestroyMenu(IntPtr hMenu);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern uint RegisterWindowMessage(string lpString);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        public event EventHandler? OnIconClicked;

        public SystemTrayIcon(Icon icon, string toolTipText, Action? onRestore = null, Action? onSettings = null, Action? onExit = null)
        {
            _hIcon = icon.Handle;
            _onRestore = onRestore;
            _onSettings = onSettings;
            _onExit = onExit;
            _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");

            var parameters = new HwndSourceParameters(WindowClass)
            {
                ParentWindow = new IntPtr(HWND_MESSAGE)
            };
            _hwndSource = new HwndSource(parameters);
            _hWnd = _hwndSource.Handle;
            _hwndSource.AddHook(WndProc);

            AddNotifyIcon(toolTipText);
        }

        private void AddNotifyIcon(string toolTipText)
        {
            var data = new NOTIFYICONDATA
            {
                hWnd = _hWnd,
                uID = 1,
                uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
                uCallbackMessage = (uint)WM_TRAYICON,
                hIcon = _hIcon,
                szTip = toolTipText ?? ""
            };
            data.cbSize = (uint)Marshal.SizeOf(data);
            Shell_NotifyIcon(NIM_ADD, ref data);
            _added = true;
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_TRAYICON)
            {
                int eventType = lParam.ToInt32() & 0xFFFF;
                switch (eventType)
                {
                    case WM_LBUTTONUP:
                        OnIconClicked?.Invoke(this, EventArgs.Empty);
                        break;
                    case WM_RBUTTONUP:
                        ShowContextMenu();
                        handled = true;
                        break;
                }
            }
            else if (msg == _taskbarCreatedMessage && _added)
            {
                var data = new NOTIFYICONDATA
                {
                    hWnd = _hWnd,
                    uID = 1,
                    uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
                    uCallbackMessage = (uint)WM_TRAYICON,
                    hIcon = _hIcon
                };
                data.cbSize = (uint)Marshal.SizeOf(data);
                Shell_NotifyIcon(NIM_ADD, ref data);
            }
            else if (msg == WM_COMMAND)
            {
                int menuId = wParam.ToInt32() & 0xFFFF;
                switch (menuId)
                {
                    case IDM_RESTORE:
                        _onRestore?.Invoke();
                        break;
                    case IDM_SETTINGS:
                        _onSettings?.Invoke();
                        break;
                    case IDM_EXIT:
                        _onExit?.Invoke();
                        break;
                }
            }

            return IntPtr.Zero;
        }

        private void ShowContextMenu()
        {
            IntPtr hMenu = CreatePopupMenu();
            AppendMenu(hMenu, MFT_STRING, (UIntPtr)IDM_RESTORE, "Restore");
            AppendMenu(hMenu, MFT_STRING, (UIntPtr)IDM_SETTINGS, "Settings...");
            AppendMenu(hMenu, MFT_SEPARATOR, UIntPtr.Zero, "");
            AppendMenu(hMenu, MFT_STRING, (UIntPtr)IDM_EXIT, "Exit");

            GetCursorPos(out POINT pt);
            SetForegroundWindow(_hWnd);

            int cmd = TrackPopupMenu(hMenu, TPM_LEFTALIGN | TPM_RETURNCMD, pt.X, pt.Y, 0, _hWnd, IntPtr.Zero);

            DestroyMenu(hMenu);

            if (cmd == IDM_RESTORE)
                _onRestore?.Invoke();
            else if (cmd == IDM_SETTINGS)
                _onSettings?.Invoke();
            else if (cmd == IDM_EXIT)
                _onExit?.Invoke();
        }

        public void Show()
        {
            if (!_added) return;
            var data = new NOTIFYICONDATA
            {
                hWnd = _hWnd,
                uID = 1
            };
            data.cbSize = (uint)Marshal.SizeOf(data);
            Shell_NotifyIcon(NIM_ADD, ref data);
        }

        public void Hide()
        {
            if (!_added) return;
            var data = new NOTIFYICONDATA
            {
                hWnd = _hWnd,
                uID = 1
            };
            data.cbSize = (uint)Marshal.SizeOf(data);
            Shell_NotifyIcon(NIM_DELETE, ref data);
        }

        public void UpdateToolTip(string text)
        {
            if (!_added) return;
            var data = new NOTIFYICONDATA
            {
                hWnd = _hWnd,
                uID = 1,
                uFlags = NIF_TIP,
                szTip = text ?? ""
            };
            data.cbSize = (uint)Marshal.SizeOf(data);
            Shell_NotifyIcon(NIM_MODIFY, ref data);
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                Hide();
                _hwndSource.RemoveHook(WndProc);
                _hwndSource.Dispose();
                _disposed = true;
            }
        }
    }
}
