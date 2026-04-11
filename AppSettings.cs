using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WpfAppTest;

public class AppSettings
{
    private static readonly string SettingsPath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public string OcrProvider { get; set; } = "ollama";
    public string OcrModel { get; set; } = "glm-ocr:q8_0";
    public string TranslationProvider { get; set; } = "google";
    public string TranslationModel { get; set; } = "gemma3:1b";
    public string OllamaBaseUrl { get; set; } = "http://localhost:11434";

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load settings: {ex.Message}");
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(this, JsonOptions);
            File.WriteAllText(SettingsPath, json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to save settings: {ex.Message}");
            throw new InvalidOperationException($"Failed to save settings to {SettingsPath}: {ex.Message}", ex);
        }
    }
}
