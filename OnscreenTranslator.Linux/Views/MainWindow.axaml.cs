using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using OnscreenTranslator.Core.Platform;
using OnscreenTranslator.Core.Providers;
using OnscreenTranslator.Core.Settings;
using OnscreenTranslator.Linux.Platform;

namespace OnscreenTranslator.Linux.Views;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings;
    private readonly IPlatformServices _platform;

    /// <summary>Design-time constructor.</summary>
    public MainWindow() : this(new AppSettings(), new LinuxPlatformServices()) { }

    public MainWindow(AppSettings settings, IPlatformServices platform)
    {
        _settings = settings;
        _platform = platform;
        InitializeComponent();

        // Bind remote config visibility
        RemoteConfigPanel.IsVisible = IsRemoteSelected();
        OcrProviderCombo.SelectionChanged += (_, _) =>
            RemoteConfigPanel.IsVisible = IsRemoteSelected();
        TranslationProviderCombo.SelectionChanged += (_, _) =>
            RemoteConfigPanel.IsVisible = IsRemoteSelected();
    }

    private bool IsRemoteSelected()
    {
        var ocrSel = (OcrProviderCombo.SelectedItem as ComboBoxItem)?.Content?.ToString();
        var trSel = (TranslationProviderCombo.SelectedItem as ComboBoxItem)?.Content?.ToString();
        return ocrSel == "Remote Server" || trSel == "Remote Server";
    }

    private async void OnCaptureClicked(object? sender, RoutedEventArgs e)
    {
        // Hide window temporarily so it doesn't appear in the screenshot
        this.Hide();

        try
        {
            // Grab the full screen bounds
            var bounds = _platform.GetScreenBounds();
            // TODO: Show a region selection overlay, then capture just that region.
            // For now, capture the full screen as a demo.
            using var bitmap = await _platform.CaptureScreenAsync(bounds);

            // Save to a temp file for OCR
            var tmp = System.IO.Path.GetTempFileName() + ".png";
            bitmap.Save(tmp, System.Drawing.Imaging.ImageFormat.Png);

            try
            {
                var ocrProvider = ProviderFactory.CreateOcrProvider(_settings);
                var ocrText = await ocrProvider.RecognizeTextAsync(tmp);

                OcrTextBox.Text = ocrText;

                if (!string.IsNullOrWhiteSpace(ocrText))
                {
                    var trProvider = ProviderFactory.CreateTranslationProvider(
                        _settings, _settings.TranslationProvider);
                    var translation = await trProvider.TranslateAsync(ocrText);
                    TranslationTextBox.Text = translation;
                }
            }
            finally
            {
                try { System.IO.File.Delete(tmp); } catch { /* best effort */ }
            }
        }
        catch (Exception ex)
        {
            OcrTextBox.Text = $"Error: {ex.Message}";
        }
        finally
        {
            this.Show();
        }
    }

    private void OnSettingsClicked(object? sender, RoutedEventArgs e)
    {
        // Save current values to settings
        SaveSettingsFromUi();
        _settings.Save();

        // TODO: Open a settings dialog
    }

    private void OnOcrProviderChanged(object? sender, SelectionChangedEventArgs e)
    {
        var selected = (OcrProviderCombo.SelectedItem as ComboBoxItem)?.Content?.ToString();
        _settings.OcrProvider = selected switch
        {
            "Ollama (Local)" => "ollama",
            "OpenAI" => "openai",
            "OpenAI-Compatible" => "openai-compatible",
            "Remote Server" => "remote",
            _ => "ollama"
        };
    }

    private void OnTranslationProviderChanged(object? sender, SelectionChangedEventArgs e)
    {
        var selected = (TranslationProviderCombo.SelectedItem as ComboBoxItem)?.Content?.ToString();
        _settings.TranslationProvider = selected switch
        {
            "Google Translate" => "google",
            "DeepL" => "deepl",
            "Ollama (Local)" => "ollama",
            "OpenAI" => "openai",
            "OpenAI-Compatible" => "openai-compatible",
            "Remote Server" => "remote",
            _ => "google"
        };
    }

    private void SaveSettingsFromUi()
    {
        _settings.RemoteServer.BaseUrl = RemoteUrlBox.Text ?? "";
        _settings.RemoteServer.ApiKey = RemoteKeyBox.Text ?? "";
        _settings.RemoteServer.RouteOcr = RemoteOcrCheck.IsChecked == true;
        _settings.RemoteServer.RouteTranslation = RemoteTranslationCheck.IsChecked == true;
        _settings.RemoteServer.RouteDictionary = RemoteDictCheck.IsChecked == true;
    }
}
