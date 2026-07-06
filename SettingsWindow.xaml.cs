using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WpfAppTest.Providers;
using WpfAppTest.Providers.Furigana;

namespace WpfAppTest;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly AppSettings _workingCopy;
    private bool _initializing = true;

    /// <summary>
    /// Fired when settings are saved and providers should be refreshed.
    /// </summary>
    public event Action? SettingsChanged;

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();

        // Work with a copy so we can cancel
        _settings = settings;
        _workingCopy = new AppSettings
        {
            OcrProvider = settings.OcrProvider,
            TranslationProvider = settings.TranslationProvider,
        };

        // Deep copy provider configs
        foreach (var kvp in settings.Providers)
        {
            _workingCopy.Providers[kvp.Key] = new ProviderConfig
            {
                BaseUrl = kvp.Value.BaseUrl,
                ApiKey = kvp.Value.ApiKey,
                OcrModel = kvp.Value.OcrModel,
                TranslationModel = kvp.Value.TranslationModel
            };
        }

        // Deep copy Furigana settings
        _workingCopy.Furigana = new FuriganaSettings
        {
            Enabled = settings.Furigana.Enabled,
            SidecarUrl = settings.Furigana.SidecarUrl,
            AutoStartSidecar = settings.Furigana.AutoStartSidecar,
            UseFlflFallback = settings.Furigana.UseFlflFallback,
            FuriganaPort = settings.Furigana.FuriganaPort,
            FlflLatencyThresholdMs = settings.Furigana.FlflLatencyThresholdMs
        };

        InitializeOcrProviderComboBox();
        InitializeTranslationProviderComboBox();
        LoadFuriganaSettings();

        _initializing = false;

        // Show initial provider settings
        UpdateOcrProviderPanel();
        UpdateTranslationProviderPanel();
    }

    #region OCR Provider

    private void InitializeOcrProviderComboBox()
    {
        OcrProviderComboBox.Items.Clear();
        foreach (var name in ProviderFactory.AvailableOcrProviders)
        {
            var display = ProviderFactory.OcrProviderDisplayNames.GetValueOrDefault(name, name);
            OcrProviderComboBox.Items.Add(display);
        }

        // See InitializeTranslationProviderComboBox: plain strings must be
        // used so WPF UI's selection box renders the text.
        int idx = Array.IndexOf(ProviderFactory.AvailableOcrProviders, _workingCopy.OcrProvider);
        OcrProviderComboBox.SelectedIndex = idx >= 0 ? idx : 0;
    }

    private void OcrProviderComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        int idx = OcrProviderComboBox.SelectedIndex;
        if (idx >= 0 && idx < ProviderFactory.AvailableOcrProviders.Length)
        {
            _workingCopy.OcrProvider = ProviderFactory.AvailableOcrProviders[idx];
            UpdateOcrProviderPanel();
        }
    }

    private void UpdateOcrProviderPanel()
    {
        OcrOllamaPanel.Visibility = Visibility.Collapsed;
        OcrOpenAIPanel.Visibility = Visibility.Collapsed;
        OcrOpenAICompatiblePanel.Visibility = Visibility.Collapsed;

        switch (_workingCopy.OcrProvider)
        {
            case "ollama":
                OcrOllamaPanel.Visibility = Visibility.Visible;
                LoadOcrOllamaSettings();
                break;
            case "openai":
                OcrOpenAIPanel.Visibility = Visibility.Visible;
                LoadOcrOpenAISettings();
                break;
            case "openai-compatible":
                OcrOpenAICompatiblePanel.Visibility = Visibility.Visible;
                LoadOcrCompatSettings();
                break;
        }
    }

    private void LoadOcrOllamaSettings()
    {
        var config = _workingCopy.GetProviderConfig("ollama");
        OcrOllamaBaseUrlTextBox.Text = config.BaseUrl;
        OcrOllamaModelComboBox.Text = config.OcrModel;
        _ = LoadModelsForComboBoxAsync(OcrOllamaModelComboBox, "ollama", config.OcrModel);
    }

    private void LoadOcrOpenAISettings()
    {
        var config = _workingCopy.GetProviderConfig("openai");
        OcrOpenAIBaseUrlTextBox.Text = string.IsNullOrEmpty(config.BaseUrl) ? "https://api.openai.com/v1" : config.BaseUrl;
        OcrOpenAIApiKeyBox.Password = config.ApiKey;
        OcrOpenAIModelComboBox.Text = config.OcrModel;
        _ = LoadModelsForComboBoxAsync(OcrOpenAIModelComboBox, "openai", config.OcrModel);
    }

    private void LoadOcrCompatSettings()
    {
        var config = _workingCopy.GetProviderConfig("openai-compatible");
        OcrCompatBaseUrlTextBox.Text = config.BaseUrl;
        OcrCompatApiKeyBox.Password = config.ApiKey;
        OcrCompatModelComboBox.Text = config.OcrModel;
        _ = LoadModelsForComboBoxAsync(OcrCompatModelComboBox, "openai-compatible", config.OcrModel);
    }

    #endregion

    #region Translation Provider

    private void InitializeTranslationProviderComboBox()
    {
        TranslationProviderComboBox.Items.Clear();
        foreach (var name in ProviderFactory.AvailableTranslationProviders)
        {
            var display = ProviderFactory.TranslationProviderDisplayNames.GetValueOrDefault(name, name);
            TranslationProviderComboBox.Items.Add(display);
        }

        // NOTE: do NOT add ComboBoxItem objects directly. WPF UI's ComboBox
        // template renders the closed selection box from SelectionBoxItem,
        // which becomes the ComboBoxItem container itself (not its text),
        // producing an empty/invisible selection box. Plain strings render
        // correctly. The provider name is recovered by index below.
        int idx = Array.IndexOf(ProviderFactory.AvailableTranslationProviders, _workingCopy.TranslationProvider);
        TranslationProviderComboBox.SelectedIndex = idx >= 0 ? idx : 0;
    }

    private void TranslationProviderComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        int idx = TranslationProviderComboBox.SelectedIndex;
        if (idx >= 0 && idx < ProviderFactory.AvailableTranslationProviders.Length)
        {
            _workingCopy.TranslationProvider = ProviderFactory.AvailableTranslationProviders[idx];
            UpdateTranslationProviderPanel();
        }
    }

    private void UpdateTranslationProviderPanel()
    {
        TransGooglePanel.Visibility = Visibility.Collapsed;
        TransDeepLPanel.Visibility = Visibility.Collapsed;
        TransOllamaPanel.Visibility = Visibility.Collapsed;
        TransOpenAIPanel.Visibility = Visibility.Collapsed;
        TransOpenAICompatiblePanel.Visibility = Visibility.Collapsed;

        switch (_workingCopy.TranslationProvider)
        {
            case "google":
                TransGooglePanel.Visibility = Visibility.Visible;
                LoadTransGoogleSettings();
                break;
            case "deepl":
                TransDeepLPanel.Visibility = Visibility.Visible;
                LoadTransDeepLSettings();
                break;
            case "ollama":
                TransOllamaPanel.Visibility = Visibility.Visible;
                LoadTransOllamaSettings();
                break;
            case "openai":
                TransOpenAIPanel.Visibility = Visibility.Visible;
                LoadTransOpenAISettings();
                break;
            case "openai-compatible":
                TransOpenAICompatiblePanel.Visibility = Visibility.Visible;
                LoadTransCompatSettings();
                break;
        }
    }

    private void LoadTransGoogleSettings()
    {
        var config = _workingCopy.GetProviderConfig("google");
        TransGoogleApiKeyBox.Password = config.ApiKey;
    }

    private void LoadTransDeepLSettings()
    {
        var config = _workingCopy.GetProviderConfig("deepl");
        TransDeepLApiKeyBox.Password = config.ApiKey;
    }

    private void LoadTransOllamaSettings()
    {
        var config = _workingCopy.GetProviderConfig("ollama");
        TransOllamaBaseUrlTextBox.Text = config.BaseUrl;
        TransOllamaModelComboBox.Text = config.TranslationModel;
        _ = LoadTranslationModelsForComboBoxAsync(TransOllamaModelComboBox, "ollama", config.TranslationModel);
    }

    private void LoadTransOpenAISettings()
    {
        var config = _workingCopy.GetProviderConfig("openai");
        TransOpenAIBaseUrlTextBox.Text = string.IsNullOrEmpty(config.BaseUrl) ? "https://api.openai.com/v1" : config.BaseUrl;
        TransOpenAIApiKeyBox.Password = config.ApiKey;
        TransOpenAIModelComboBox.Text = config.TranslationModel;
        _ = LoadTranslationModelsForComboBoxAsync(TransOpenAIModelComboBox, "openai", config.TranslationModel);
    }

    private void LoadTransCompatSettings()
    {
        var config = _workingCopy.GetProviderConfig("openai-compatible");
        TransCompatBaseUrlTextBox.Text = config.BaseUrl;
        TransCompatApiKeyBox.Password = config.ApiKey;
        TransCompatModelComboBox.Text = config.TranslationModel;
        _ = LoadTranslationModelsForComboBoxAsync(TransCompatModelComboBox, "openai-compatible", config.TranslationModel);
    }

    #endregion

    #region Model Loading

    private async Task LoadModelsForComboBoxAsync(ComboBox comboBox, string providerName, string currentModel)
    {
        try
        {
            // Collect current UI values first, then build settings
            CollectOcrSettings();
            var tempSettings = BuildTempSettings();
            var models = await ProviderFactory.ListModelsForProvider(providerName, tempSettings);

            var prevText = comboBox.Text;
            comboBox.Items.Clear();
            foreach (var model in models)
            {
                comboBox.Items.Add(model);
            }

            // Restore previous text/selection
            if (!string.IsNullOrEmpty(prevText))
                comboBox.Text = prevText;
            else if (!string.IsNullOrEmpty(currentModel))
                comboBox.Text = currentModel;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to load models for {providerName}: {ex.Message}");
        }
    }

    private async Task LoadTranslationModelsForComboBoxAsync(ComboBox comboBox, string providerName, string currentModel)
    {
        try
        {
            // Collect current UI values first, then build settings
            CollectTranslationSettings();
            var tempSettings = BuildTempSettings();

            List<string> models;
            if (providerName == "ollama")
            {
                var config = tempSettings.GetProviderConfig("ollama");
                using var provider = new OllamaTranslationProvider
                {
                    BaseUrl = config.BaseUrl,
                    Model = config.TranslationModel
                };
                models = await provider.ListModelsAsync();
            }
            else if (providerName == "openai" || providerName == "openai-compatible")
            {
                using var provider = new OpenAITranslationProvider(providerName, providerName)
                {
                    BaseUrl = tempSettings.GetProviderConfig(providerName).BaseUrl,
                    ApiKey = tempSettings.GetProviderConfig(providerName).ApiKey,
                    Model = currentModel
                };
                models = await provider.ListModelsAsync();
            }
            else
            {
                return;
            }

            var prevText = comboBox.Text;
            comboBox.Items.Clear();
            foreach (var model in models)
            {
                comboBox.Items.Add(model);
            }

            if (!string.IsNullOrEmpty(prevText))
                comboBox.Text = prevText;
            else if (!string.IsNullOrEmpty(currentModel))
                comboBox.Text = currentModel;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to load translation models for {providerName}: {ex.Message}");
        }
    }

    private void RefreshOcrModels_Click(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        var providerName = _workingCopy.OcrProvider;
        ComboBox targetCombo = providerName switch
        {
            "ollama" => OcrOllamaModelComboBox,
            "openai" => OcrOpenAIModelComboBox,
            "openai-compatible" => OcrCompatModelComboBox,
            _ => OcrOllamaModelComboBox
        };

        // Read current values from UI into working copy first
        CollectOcrSettings();
        _ = LoadModelsForComboBoxAsync(targetCombo, providerName, targetCombo.Text);
    }

    private void RefreshTranslationModels_Click(object sender, RoutedEventArgs e)
    {
        var providerName = _workingCopy.TranslationProvider;
        ComboBox targetCombo = providerName switch
        {
            "ollama" => TransOllamaModelComboBox,
            "openai" => TransOpenAIModelComboBox,
            "openai-compatible" => TransCompatModelComboBox,
            _ => TransOllamaModelComboBox
        };

        CollectTranslationSettings();
        _ = LoadTranslationModelsForComboBoxAsync(targetCombo, providerName, targetCombo.Text);
    }

    #endregion

    #region Test Connection

    private async void TestOcrConnection_Click(object sender, RoutedEventArgs e)
    {
        CollectOcrSettings();
        OcrConnectionStatus.Text = "Testing...";
        OcrConnectionStatus.Foreground = new SolidColorBrush(Colors.Gray);

        try
        {
            var provider = ProviderFactory.CreateOcrProvider(_workingCopy);
            var available = await provider.IsAvailableAsync();

            if (provider is IDisposable disposable)
                disposable.Dispose();

            if (available)
            {
                OcrConnectionStatus.Text = "✓ Connected";
                OcrConnectionStatus.Foreground = new SolidColorBrush(Color.FromRgb(76, 175, 80));
            }
            else
            {
                OcrConnectionStatus.Text = "✗ Unavailable";
                OcrConnectionStatus.Foreground = new SolidColorBrush(Color.FromRgb(244, 67, 54));
            }
        }
        catch (Exception ex)
        {
            OcrConnectionStatus.Text = $"✗ Error: {ex.Message}";
            OcrConnectionStatus.Foreground = new SolidColorBrush(Color.FromRgb(244, 67, 54));
        }
    }

    private async void TestTranslationConnection_Click(object sender, RoutedEventArgs e)
    {
        CollectTranslationSettings();
        TranslationConnectionStatus.Text = "Testing...";
        TranslationConnectionStatus.Foreground = new SolidColorBrush(Colors.Gray);

        try
        {
            var providers = ProviderFactory.CreateTranslationProviders(_workingCopy);
            var provider = providers.FirstOrDefault(p => p.Name == _workingCopy.TranslationProvider) ?? providers[0];
            var available = await provider.IsAvailableAsync();

            foreach (var p in providers)
                if (p is IDisposable d) d.Dispose();

            if (available)
            {
                TranslationConnectionStatus.Text = "✓ Connected";
                TranslationConnectionStatus.Foreground = new SolidColorBrush(Color.FromRgb(76, 175, 80));
            }
            else
            {
                TranslationConnectionStatus.Text = "✗ Unavailable";
                TranslationConnectionStatus.Foreground = new SolidColorBrush(Color.FromRgb(244, 67, 54));
            }
        }
        catch (Exception ex)
        {
            TranslationConnectionStatus.Text = $"✗ Error: {ex.Message}";
            TranslationConnectionStatus.Foreground = new SolidColorBrush(Color.FromRgb(244, 67, 54));
        }
    }

    #endregion

    #region Collect & Save

    /// <summary>
    /// Reads current OCR UI values into the working copy.
    /// </summary>
    private void CollectOcrSettings()
    {
        switch (_workingCopy.OcrProvider)
        {
            case "ollama":
                {
                    var c = _workingCopy.GetProviderConfig("ollama");
                    c.BaseUrl = OcrOllamaBaseUrlTextBox.Text.Trim();
                    c.OcrModel = OcrOllamaModelComboBox.Text.Trim();
                    break;
                }
            case "openai":
                {
                    var c = _workingCopy.GetProviderConfig("openai");
                    c.BaseUrl = OcrOpenAIBaseUrlTextBox.Text.Trim();
                    c.ApiKey = OcrOpenAIApiKeyBox.Password.Trim();
                    c.OcrModel = OcrOpenAIModelComboBox.Text.Trim();
                    break;
                }
            case "openai-compatible":
                {
                    var c = _workingCopy.GetProviderConfig("openai-compatible");
                    c.BaseUrl = OcrCompatBaseUrlTextBox.Text.Trim();
                    c.ApiKey = OcrCompatApiKeyBox.Password.Trim();
                    c.OcrModel = OcrCompatModelComboBox.Text.Trim();
                    break;
                }
        }
    }

    /// <summary>
    /// Reads current Translation UI values into the working copy.
    /// </summary>
    private void CollectTranslationSettings()
    {
        switch (_workingCopy.TranslationProvider)
        {
            case "google":
                {
                    var c = _workingCopy.GetProviderConfig("google");
                    c.ApiKey = TransGoogleApiKeyBox.Password.Trim();
                    break;
                }
            case "deepl":
                {
                    var c = _workingCopy.GetProviderConfig("deepl");
                    c.ApiKey = TransDeepLApiKeyBox.Password.Trim();
                    break;
                }
            case "ollama":
                {
                    var c = _workingCopy.GetProviderConfig("ollama");
                    c.BaseUrl = TransOllamaBaseUrlTextBox.Text.Trim();
                    c.TranslationModel = TransOllamaModelComboBox.Text.Trim();
                    break;
                }
            case "openai":
                {
                    var c = _workingCopy.GetProviderConfig("openai");
                    c.BaseUrl = TransOpenAIBaseUrlTextBox.Text.Trim();
                    c.ApiKey = TransOpenAIApiKeyBox.Password.Trim();
                    c.TranslationModel = TransOpenAIModelComboBox.Text.Trim();
                    break;
                }
            case "openai-compatible":
                {
                    var c = _workingCopy.GetProviderConfig("openai-compatible");
                    c.BaseUrl = TransCompatBaseUrlTextBox.Text.Trim();
                    c.ApiKey = TransCompatApiKeyBox.Password.Trim();
                    c.TranslationModel = TransCompatModelComboBox.Text.Trim();
                    break;
                }
        }
    }

    /// <summary>
    /// Builds a temporary AppSettings from current working copy state (for model listing).
    /// Does NOT mutate _workingCopy. Call CollectOcrSettings/CollectTranslationSettings
    /// explicitly before calling this if you need current UI values.
    /// </summary>
    private AppSettings BuildTempSettings()
    {
        var temp = new AppSettings
        {
            OcrProvider = _workingCopy.OcrProvider,
            TranslationProvider = _workingCopy.TranslationProvider,
        };

        // Copy working copy providers
        foreach (var kvp in _workingCopy.Providers)
        {
            temp.Providers[kvp.Key] = new ProviderConfig
            {
                BaseUrl = kvp.Value.BaseUrl,
                ApiKey = kvp.Value.ApiKey,
                OcrModel = kvp.Value.OcrModel,
                TranslationModel = kvp.Value.TranslationModel
            };
        }

        return temp;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        CollectOcrSettings();
        CollectTranslationSettings();
        CollectFuriganaSettings();

        // Copy working copy back to real settings
        _settings.OcrProvider = _workingCopy.OcrProvider;
        _settings.TranslationProvider = _workingCopy.TranslationProvider;

        foreach (var kvp in _workingCopy.Providers)
        {
            var config = _settings.GetProviderConfig(kvp.Key);
            config.BaseUrl = kvp.Value.BaseUrl;
            config.ApiKey = kvp.Value.ApiKey;
            config.OcrModel = kvp.Value.OcrModel;
            config.TranslationModel = kvp.Value.TranslationModel;
        }

        // Copy Furigana settings back
        _settings.Furigana.Enabled = _workingCopy.Furigana.Enabled;
        _settings.Furigana.SidecarUrl = _workingCopy.Furigana.SidecarUrl;
        _settings.Furigana.AutoStartSidecar = _workingCopy.Furigana.AutoStartSidecar;
        _settings.Furigana.UseFlflFallback = _workingCopy.Furigana.UseFlflFallback;
        _settings.Furigana.FuriganaPort = _workingCopy.Furigana.FuriganaPort;
        _settings.Furigana.FlflLatencyThresholdMs = _workingCopy.Furigana.FlflLatencyThresholdMs;

        try
        {
            _settings.Save();
            SettingsChanged?.Invoke();
            SetDialogResult(true);
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to save settings: {ex.Message}", "Save Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        SetDialogResult(false);
        Close();
    }

    private void SetDialogResult(bool? value)
    {
        try
        {
            DialogResult = value;
        }
        catch (InvalidOperationException)
        {
            // DialogResult can only be set if the window was shown using ShowDialog().
            // If shown using Show(), setting it throws an exception, which we can safely ignore.
        }
    }

    #endregion

    #region Furigana Settings

    private void LoadFuriganaSettings()
    {
        FuriganaEnabledCheckBox.IsChecked = _workingCopy.Furigana.Enabled;
        FuriganaSidecarUrlTextBox.Text = _workingCopy.Furigana.SidecarUrl;
        FuriganaAutoStartCheckBox.IsChecked = _workingCopy.Furigana.AutoStartSidecar;
        FuriganaUseFlflCheckBox.IsChecked = _workingCopy.Furigana.UseFlflFallback;

        // Subscribe to status changes from the shared service manager
        FuriganaServiceManager.Instance.StatusChanged += OnFuriganaStatusChanged;

        // Ensure we unsubscribe when the window is closed (prevents event handler leak on singleton)
        Closed += (s, e) => FuriganaServiceManager.Instance.StatusChanged -= OnFuriganaStatusChanged;

        // Show current status
        UpdateFuriganaStatusFromManager();
    }

    private void CollectFuriganaSettings()
    {
        _workingCopy.Furigana.Enabled = FuriganaEnabledCheckBox.IsChecked == true;
        _workingCopy.Furigana.SidecarUrl = FuriganaSidecarUrlTextBox.Text.Trim();
        _workingCopy.Furigana.AutoStartSidecar = FuriganaAutoStartCheckBox.IsChecked == true;
        _workingCopy.Furigana.UseFlflFallback = FuriganaUseFlflCheckBox.IsChecked == true;
    }

    private void OnFuriganaStatusChanged(object? sender, FuriganaServiceStatusChangedEventArgs e)
    {
        Dispatcher.InvokeAsync(() => UpdateFuriganaStatusFromManager());
    }

    private async void UpdateFuriganaStatusFromManager()
    {
        var mgr = FuriganaServiceManager.Instance;

        if (!mgr.IsRunning)
        {
            FuriganaStatusText.Text = "Not running";
            FuriganaStatusText.Foreground = new SolidColorBrush(Colors.Gray);
            return;
        }

        try
        {
            var status = await mgr.GetStatusAsync();
            if (status == null)
            {
                FuriganaStatusText.Text = "Running (status unknown)";
                FuriganaStatusText.Foreground = new SolidColorBrush(Color.FromRgb(255, 193, 7)); // amber
                return;
            }

            string text;
            if (status.SudachiReady && status.FlflLoaded && status.FlflLatencyMs.HasValue)
                text = $"Sudachi ready, FLFL ready (latency: {status.FlflLatencyMs.Value:F0}ms)";
            else if (status.SudachiReady && status.FlflLoading)
                text = "Sudachi ready, FLFL loading…";
            else if (status.SudachiReady)
                text = "Sudachi ready, FLFL not loaded";
            else
                text = "Initializing…";

            FuriganaStatusText.Text = text;
            FuriganaStatusText.Foreground = new SolidColorBrush(Color.FromRgb(76, 175, 80)); // green
        }
        catch
        {
            FuriganaStatusText.Text = "Running (unable to fetch status)";
            FuriganaStatusText.Foreground = new SolidColorBrush(Color.FromRgb(255, 193, 7));
        }
    }

    private async void FuriganaTestConnection_Click(object sender, RoutedEventArgs e)
    {
        CollectFuriganaSettings();

        var url = _workingCopy.Furigana.SidecarUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            FuriganaConnectionStatus.Text = "✗ No URL configured";
            FuriganaConnectionStatus.Foreground = new SolidColorBrush(Color.FromRgb(244, 67, 54));
            return;
        }

        FuriganaConnectionStatus.Text = "Testing…";
        FuriganaConnectionStatus.Foreground = new SolidColorBrush(Colors.Gray);

        try
        {
            using var provider = new HttpFuriganaProvider(url);
            var available = await provider.IsAvailableAsync();

            if (available)
            {
                FuriganaConnectionStatus.Text = "✓ Connected";
                FuriganaConnectionStatus.Foreground = new SolidColorBrush(Color.FromRgb(76, 175, 80));

                // Also refresh the detailed status display
                await UpdateFuriganaStatusFromManagerAsync();
            }
            else
            {
                FuriganaConnectionStatus.Text = "✗ Not reachable";
                FuriganaConnectionStatus.Foreground = new SolidColorBrush(Color.FromRgb(244, 67, 54));
            }
        }
        catch (Exception ex)
        {
            FuriganaConnectionStatus.Text = $"✗ Error: {ex.Message}";
            FuriganaConnectionStatus.Foreground = new SolidColorBrush(Color.FromRgb(244, 67, 54));
        }
    }

    private async Task UpdateFuriganaStatusFromManagerAsync()
    {
        var mgr = FuriganaServiceManager.Instance;
        if (!mgr.IsRunning) return;

        try
        {
            var status = await mgr.GetStatusAsync();
            if (status == null) return;

            string text;
            if (status.SudachiReady && status.FlflLoaded && status.FlflLatencyMs.HasValue)
                text = $"Sudachi ready, FLFL ready (latency: {status.FlflLatencyMs.Value:F0}ms)";
            else if (status.SudachiReady && status.FlflLoading)
                text = "Sudachi ready, FLFL loading…";
            else if (status.SudachiReady)
                text = "Sudachi ready, FLFL not loaded";
            else
                text = "Initializing…";

            FuriganaStatusText.Text = text;
            FuriganaStatusText.Foreground = new SolidColorBrush(Color.FromRgb(76, 175, 80));
        }
        catch { }
    }

    private void FuriganaInstall_Click(object sender, RoutedEventArgs e)
    {
        string serviceDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "furigana-service");
        bool isWindows = OperatingSystem.IsWindows();
        string scriptName = isWindows ? "install.bat" : "install.sh";
        string scriptPath = Path.Combine(serviceDir, scriptName);

        if (!File.Exists(scriptPath))
        {
            MessageBox.Show(
                $"Installation script not found:\n{scriptPath}\n\nPlease ensure the furigana-service directory is included in the application.",
                "Install Sidecar",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = isWindows ? scriptPath : "/bin/bash",
                Arguments = isWindows ? "" : $"\"{scriptPath}\"",
                WorkingDirectory = serviceDir,
                UseShellExecute = true,
                CreateNoWindow = false
            };
            Process.Start(startInfo);

            MessageBox.Show(
                "Installation started. The sidecar setup process will open.\n\n" +
                "After installation completes, click \"Start\" to launch the sidecar.\n\n" +
                $"Check the logs at:\n%LOCALAPPDATA%/OnscreenTranslator/logs/furigana-service.log",
                "Install Sidecar",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Failed to start installation:\n{ex.Message}",
                "Install Sidecar",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async void FuriganaStart_Click(object sender, RoutedEventArgs e)
    {
        CollectFuriganaSettings();

        FuriganaStatusText.Text = "Starting…";
        FuriganaStatusText.Foreground = new SolidColorBrush(Colors.Gray);

        try
        {
            var mgr = FuriganaServiceManager.Instance;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(35));
            await mgr.StartAsync(cts.Token);

            FuriganaStatusText.Text = "Running";
            FuriganaStatusText.Foreground = new SolidColorBrush(Color.FromRgb(76, 175, 80));
            await UpdateFuriganaStatusFromManagerAsync();
        }
        catch (OperationCanceledException)
        {
            FuriganaStatusText.Text = "Error: start timed out";
            FuriganaStatusText.Foreground = new SolidColorBrush(Color.FromRgb(244, 67, 54));
        }
        catch (Exception ex)
        {
            FuriganaStatusText.Text = $"Error: {ex.Message}";
            FuriganaStatusText.Foreground = new SolidColorBrush(Color.FromRgb(244, 67, 54));
        }
    }

    private async void FuriganaStop_Click(object sender, RoutedEventArgs e)
    {
        FuriganaStatusText.Text = "Stopping…";
        FuriganaStatusText.Foreground = new SolidColorBrush(Colors.Gray);

        try
        {
            var mgr = FuriganaServiceManager.Instance;
            await mgr.StopAsync();

            FuriganaStatusText.Text = "Not running";
            FuriganaStatusText.Foreground = new SolidColorBrush(Colors.Gray);
        }
        catch (Exception ex)
        {
            FuriganaStatusText.Text = $"Error: {ex.Message}";
            FuriganaStatusText.Foreground = new SolidColorBrush(Color.FromRgb(244, 67, 54));
        }
    }

    #endregion
}
