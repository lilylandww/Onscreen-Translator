using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using OnscreenTranslator.Core.Platform;
using OnscreenTranslator.Core.Settings;
using OnscreenTranslator.Linux.Platform;
using OnscreenTranslator.Linux.Views;

namespace OnscreenTranslator.Linux;

public class App : Application
{
    private readonly AppSettings _settings;
    private readonly IPlatformServices _platform;

    /// <summary>Design-time constructor.</summary>
    public App() : this(new AppSettings(), new LinuxPlatformServices()) { }

    public App(AppSettings settings, IPlatformServices platform)
    {
        _settings = settings;
        _platform = platform;
    }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow(_settings, _platform);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
