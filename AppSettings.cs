using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WpfAppTest;

public class ProviderConfig
{
    public string BaseUrl { get; set; } = "";

    [JsonConverter(typeof(ProtectedStringConverter))]
    public string ApiKey { get; set; } = "";

    public string OcrModel { get; set; } = "";
    public string TranslationModel { get; set; } = "";
}

/// <summary>
/// JSON converter that encrypts API keys with DPAPI (DataProtectionScope.CurrentUser)
/// before writing to disk. Serialized format: "enc:" + Base64(encrypted bytes).
/// Backward-compatible with plaintext values (reads plaintext as-is).
/// </summary>
public class ProtectedStringConverter : JsonConverter<string>
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("WpfAppTest-ApiKey-Protection-v1");
    private const string EncryptedPrefix = "enc:";

    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetString();
        if (string.IsNullOrEmpty(value)) return value;

        // If encrypted, decrypt
        if (value.StartsWith(EncryptedPrefix, StringComparison.Ordinal))
        {
            try
            {
                var encryptedBytes = Convert.FromBase64String(value[EncryptedPrefix.Length..]);
                var decryptedBytes = ProtectedData.Unprotect(encryptedBytes, Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(decryptedBytes);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to decrypt API key: {ex.Message}");
                return string.Empty;
            }
        }

        // Plaintext — return as-is (backward compatibility)
        return value;
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        if (string.IsNullOrEmpty(value))
        {
            writer.WriteStringValue(value);
            return;
        }

        try
        {
            var plainBytes = Encoding.UTF8.GetBytes(value);
            var encryptedBytes = ProtectedData.Protect(plainBytes, Entropy, DataProtectionScope.CurrentUser);
            writer.WriteStringValue(EncryptedPrefix + Convert.ToBase64String(encryptedBytes));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to encrypt API key: {ex.Message}");
            // Fall back to plaintext if encryption fails
            writer.WriteStringValue(value);
        }
    }
}

public class FuriganaSettings
{
    public bool Enabled { get; set; } = false;
    public string SidecarUrl { get; set; } = "http://127.0.0.1:8765";
    public bool AutoStartSidecar { get; set; } = true;
    public bool UseFlflFallback { get; set; } = true;
    public int FuriganaPort { get; set; } = 8765;
    public int FlflLatencyThresholdMs { get; set; } = 2000;
}

public class AppSettings
{
    private static readonly string SettingsPath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(), new ProtectedStringConverter() }
    };

    // Active provider selection
    public string OcrProvider { get; set; } = "ollama";
    public string TranslationProvider { get; set; } = "google";

    // Per-provider configurations
    public Dictionary<string, ProviderConfig> Providers { get; set; } = new()
    {
        ["ollama"] = new ProviderConfig
        {
            BaseUrl = "http://localhost:11434",
            OcrModel = "glm-ocr:q8_0",
            TranslationModel = "gemma3:1b"
        },
        ["openai"] = new ProviderConfig
        {
            BaseUrl = "https://api.openai.com/v1",
            OcrModel = "gpt-4o",
            TranslationModel = "gpt-4o-mini"
        },
        ["openai-compatible"] = new ProviderConfig
        {
            BaseUrl = "http://localhost:8080/v1",
            OcrModel = "",
            TranslationModel = ""
        },
        ["google"] = new ProviderConfig
        {
            ApiKey = ""
        },
        ["deepl"] = new ProviderConfig
        {
            ApiKey = ""
        }
    };

    // Furigana sidecar configuration
    public FuriganaSettings Furigana { get; set; } = new();

    // Last picked display index (for screen switch persistence)
    public int LastScreenIndex { get; set; } = -1;

    // Live (non-frozen) capture mode
    public bool LiveMode { get; set; } = false;

    // Convenience accessors
    public ProviderConfig GetProviderConfig(string providerName)
    {
        if (!Providers.TryGetValue(providerName, out var config))
        {
            config = new ProviderConfig();
            Providers[providerName] = config;
        }
        return config;
    }

    // Backward compatibility properties
    [JsonIgnore]
    public string OcrModel
    {
        get => GetProviderConfig(OcrProvider).OcrModel;
        set => GetProviderConfig(OcrProvider).OcrModel = value;
    }

    [JsonIgnore]
    public string TranslationModel
    {
        get => GetProviderConfig(TranslationProvider).TranslationModel;
        set => GetProviderConfig(TranslationProvider).TranslationModel = value;
    }

    [JsonIgnore]
    public string OllamaBaseUrl
    {
        get => GetProviderConfig("ollama").BaseUrl;
        set => GetProviderConfig("ollama").BaseUrl = value;
    }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (settings != null)
                {
                    // Ensure all default provider configs exist
                    EnsureDefaultProviders(settings);
                    return settings;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load settings: {ex.Message}");
        }
        return new AppSettings();
    }

    private static void EnsureDefaultProviders(AppSettings settings)
    {
        var defaults = new AppSettings();
        foreach (var kvp in defaults.Providers)
        {
            if (!settings.Providers.ContainsKey(kvp.Key))
            {
                settings.Providers[kvp.Key] = kvp.Value;
            }
            else
            {
                // Ensure the existing config has the base URL if it's empty
                var existing = settings.Providers[kvp.Key];
                if (string.IsNullOrEmpty(existing.BaseUrl) && !string.IsNullOrEmpty(kvp.Value.BaseUrl))
                {
                    existing.BaseUrl = kvp.Value.BaseUrl;
                }
            }
        }
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
