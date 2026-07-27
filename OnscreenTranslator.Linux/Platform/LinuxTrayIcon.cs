using System;
using OnscreenTranslator.Core.Platform;

namespace OnscreenTranslator.Linux.Platform;

/// <summary>
/// System tray icon using the StatusNotifierItem DBus protocol or libappindicator.
/// </summary>
internal class LinuxTrayIcon : IDisposable
{
    private readonly string _iconPath;
    private readonly SystemTrayMenu _menu;

    public LinuxTrayIcon(string iconPath, SystemTrayMenu menu)
    {
        _iconPath = iconPath;
        _menu = menu;
        // TODO: Register a StatusNotifierItem via DBus, or use libappindicator-sharp
    }

    public void Dispose()
    {
        // TODO: Unregister the tray icon
    }
}
