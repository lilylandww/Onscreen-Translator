using System;
using Avalonia;
using OnscreenTranslator.Core.Settings;
using OnscreenTranslator.Core.Platform;
using OnscreenTranslator.Linux.Platform;

namespace OnscreenTranslator.Linux;

class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Wire up the platform services before loading settings
        var platform = new LinuxPlatformServices();
        ProtectedStringConverter.PlatformServices = platform;

        // Load (or create) settings
        var settings = AppSettings.Load();

        // Build and run the Avalonia app
        BuildAvaloniaApp(settings, platform)
            .StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp(AppSettings settings, IPlatformServices platform)
    {
        return AppBuilder.Configure(() => new App(settings, platform))
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
    }
}
