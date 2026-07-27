using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OnscreenTranslator.Core.Platform;

namespace OnscreenTranslator.Core.Settings;

public class ProviderConfig
{
    public string BaseUrl { get; set; } = "";

    [JsonConverter(typeof(ProtectedStringConverter))]
    public string ApiKey { get; set; } = "";

    public string OcrModel { get; set; } = "";
    public string TranslationModel { get; set; } = "";
}

/// <summary>
/// Remote server configuration — point the app at a remote backend
/// instead of running OCR/translation locally.
/// </summary>
public class RemoteServerConfig
{
    /// <summary>When true, OCR and translation requests are routed to the remote server.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>Base URL of the remote backend (e.g. "http://192.168.1.100:5000").</summary>
    public string BaseUrl { get; set; } = "http://localhost:5000";

    /// <summary>Bearer token or API key for the remote server.</summary>
    [JsonConverter(typeof(ProtectedStringConverter))]
    public string ApiKey { get; set; } = "";

    /// <summary>Route OCR requests remotely.</summary>
    public bool RouteOcr { get; set; } = true;

    /// <summary>Route translation requests remotely.</summary>
    public bool RouteTranslation { get; set; } = true;

    /// <summary>Route dictionary lookups remotely.</summary>
    public bool RouteDictionary { get; set; } = true;
}

/// <summary>
/// JSON converter that encrypts sensitive strings before writing to disk.
/// Uses the platform-provided data protection (DPAPI on Windows,
/// libsecret/file-based on Linux).
/// Serialized format: "enc:" + Base64(encrypted bytes).
/// Backward-compatible with plaintext values.
/// </summary>
public class ProtectedStringConverter : JsonConverter<string>
{
    /// <summary>
    /// Set this at startup to enable encryption. When null, strings are
    /// stored as plaintext (useful for development or when platform
    /// protection is unavailable).
    /// </summary>
    public static IPlatformServices? PlatformServices { get; set; }

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("OnscreenTranslator-ApiKey-Protection-v1");
    private const string EncryptedPrefix = "enc:";

    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetString();
        if (string.IsNullOrEmpty(value)) return value;

        if (value.StartsWith(EncryptedPrefix, StringComparison.Ordinal))
        {
            try
            {
                var encryptedBytes = Convert.FromBase64String(value[EncryptedPrefix.Length..]);
                var decryptedBytes = PlatformServices != null
                    ? PlatformServices.UnprotectData(encryptedBytes, Entropy)
                    : DecryptFallback(encryptedBytes);
                return Encoding.UTF8.GetString(decryptedBytes);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to decrypt: {ex.Message}");
                return string.Empty;
            }
        }

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
            var encryptedBytes = PlatformServices != null
                ? PlatformServices.ProtectData(plainBytes, Entropy)
                : EncryptFallback(plainBytes);
            writer.WriteStringValue(EncryptedPrefix + Convert.ToBase64String(encryptedBytes));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to encrypt: {ex.Message}");
            writer.WriteStringValue(value);
        }
    }

    // Simple XOR obfuscation fallback when no platform protection is available.
    // Not cryptographically strong, but better than plaintext on disk.
    private static byte[] EncryptFallback(byte[] data)
    {
        var result = new byte[data.Length];
        for (int i = 0; i < data.Length; i++)
            result[i] = (byte)(data[i] ^ Entropy[i % Entropy.Length]);
        return result;
    }

    private static byte[] DecryptFallback(byte[] data)
    {
        return EncryptFallback(data); // XOR is symmetric
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
    private static string SettingsPath =>
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(), new ProtectedStringConverter() }
    };

    // ── Provider selection ────────────────────────────────────────────
    public string OcrProvider { get; set; } = "ollama";
    public string TranslationProvider { get; set; } = "google";

    // ── Remote server ─────────────────────────────────────────────────
    public RemoteServerConfig RemoteServer { get; set; } = new();

    // ── Per-provider configurations ────────────────────────────────────
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
        ["google"] = new ProviderConfig { ApiKey = "" },
        ["deepl"] = new ProviderConfig { ApiKey = "" }
    };

    // ── Furigana ──────────────────────────────────────────────────────
    public FuriganaSettings Furigana { get; set; } = new();

    // ── Display & capture ─────────────────────────────────────────────
    public int LastScreenIndex { get; set; } = -1;
    public bool LiveMode { get; set; } = false;

    // ── Convenience accessors ─────────────────────────────────────────
    public ProviderConfig GetProviderConfig(string providerName)
    {
        if (!Providers.TryGetValue(providerName, out var config))
        {
            config = new ProviderConfig();
            Providers[providerName] = config;
        }
        return config;
    }

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

    // ── Persistence ───────────────────────────────────────────────────
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
                settings.Providers[kvp.Key] = kvp.Value;
            else if (string.IsNullOrEmpty(settings.Providers[kvp.Key].BaseUrl)
                     && !string.IsNullOrEmpty(kvp.Value.BaseUrl))
                settings.Providers[kvp.Key].BaseUrl = kvp.Value.BaseUrl;
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
            throw new InvalidOperationException(
                $"Failed to save settings to {SettingsPath}: {ex.Message}", ex);
        }
    }
}
