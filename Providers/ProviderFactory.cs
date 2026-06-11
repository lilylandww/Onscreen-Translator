using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WpfAppTest.Providers;

/// <summary>
/// Factory for creating OCR and translation providers from application settings.
/// Centralizes provider instantiation and configuration.
/// </summary>
public static class ProviderFactory
{
    /// <summary>
    /// All available OCR provider names.
    /// </summary>
    public static readonly string[] AvailableOcrProviders = ["ollama", "openai", "openai-compatible"];

    /// <summary>
    /// All available translation provider names.
    /// </summary>
    public static readonly string[] AvailableTranslationProviders = ["google", "deepl", "ollama", "openai", "openai-compatible"];

    /// <summary>
    /// Display names for OCR providers.
    /// </summary>
    public static readonly Dictionary<string, string> OcrProviderDisplayNames = new()
    {
        ["ollama"] = "Ollama (Local)",
        ["openai"] = "OpenAI",
        ["openai-compatible"] = "OpenAI-Compatible"
    };

    /// <summary>
    /// Display names for translation providers.
    /// </summary>
    public static readonly Dictionary<string, string> TranslationProviderDisplayNames = new()
    {
        ["google"] = "Google Translate",
        ["deepl"] = "DeepL",
        ["ollama"] = "Ollama (Local)",
        ["openai"] = "OpenAI",
        ["openai-compatible"] = "OpenAI-Compatible"
    };

    /// <summary>
    /// Creates an OCR provider based on the given settings.
    /// </summary>
    public static IOCRProvider CreateOcrProvider(AppSettings settings)
    {
        return CreateOcrProvider(settings, settings.OcrProvider);
    }

    /// <summary>
    /// Creates a specific OCR provider by name.
    /// </summary>
    public static IOCRProvider CreateOcrProvider(AppSettings settings, string providerName)
    {
        var config = settings.GetProviderConfig(providerName);

        return providerName switch
        {
            "ollama" => new OllamaOCRProvider
            {
                BaseUrl = config.BaseUrl,
                Model = config.OcrModel
            },
            "openai" => new OpenAIOCRProvider("openai", "OpenAI")
            {
                BaseUrl = string.IsNullOrEmpty(config.BaseUrl) ? "https://api.openai.com/v1" : config.BaseUrl,
                ApiKey = config.ApiKey,
                Model = config.OcrModel
            },
            "openai-compatible" => new OpenAIOCRProvider("openai-compatible", "OpenAI-Compatible")
            {
                BaseUrl = config.BaseUrl,
                ApiKey = config.ApiKey,
                Model = config.OcrModel
            },
            _ => new OllamaOCRProvider
            {
                BaseUrl = settings.GetProviderConfig("ollama").BaseUrl,
                Model = settings.GetProviderConfig("ollama").OcrModel
            }
        };
    }

    /// <summary>
    /// Creates all available translation providers based on settings.
    /// </summary>
    public static List<ITranslationProvider> CreateTranslationProviders(AppSettings settings)
    {
        var providers = new List<ITranslationProvider>();

        // Google
        var googleConfig = settings.GetProviderConfig("google");
        providers.Add(new GoogleTranslationProvider(googleConfig.ApiKey));

        // DeepL
        var deeplConfig = settings.GetProviderConfig("deepl");
        providers.Add(new DeepLTranslationProvider(deeplConfig.ApiKey));

        // Ollama
        var ollamaConfig = settings.GetProviderConfig("ollama");
        providers.Add(new OllamaTranslationProvider
        {
            BaseUrl = ollamaConfig.BaseUrl,
            Model = ollamaConfig.TranslationModel
        });

        // OpenAI
        var openaiConfig = settings.GetProviderConfig("openai");
        providers.Add(new OpenAITranslationProvider("openai", "OpenAI")
        {
            BaseUrl = string.IsNullOrEmpty(openaiConfig.BaseUrl) ? "https://api.openai.com/v1" : openaiConfig.BaseUrl,
            ApiKey = openaiConfig.ApiKey,
            Model = openaiConfig.TranslationModel
        });

        // OpenAI-Compatible
        var compatConfig = settings.GetProviderConfig("openai-compatible");
        providers.Add(new OpenAITranslationProvider("openai-compatible", "OpenAI-Compatible")
        {
            BaseUrl = compatConfig.BaseUrl,
            ApiKey = compatConfig.ApiKey,
            Model = compatConfig.TranslationModel
        });

        return providers;
    }

    /// <summary>
    /// Creates a single translation provider by name.
    /// </summary>
    public static ITranslationProvider CreateTranslationProvider(AppSettings settings, string providerName)
    {
        var config = settings.GetProviderConfig(providerName);

        return providerName switch
        {
            "google" => new GoogleTranslationProvider(config.ApiKey),
            "deepl" => new DeepLTranslationProvider(config.ApiKey),
            "ollama" => new OllamaTranslationProvider
            {
                BaseUrl = config.BaseUrl,
                Model = config.TranslationModel
            },
            "openai" => new OpenAITranslationProvider("openai", "OpenAI")
            {
                BaseUrl = string.IsNullOrEmpty(config.BaseUrl) ? "https://api.openai.com/v1" : config.BaseUrl,
                ApiKey = config.ApiKey,
                Model = config.TranslationModel
            },
            "openai-compatible" => new OpenAITranslationProvider("openai-compatible", "OpenAI-Compatible")
            {
                BaseUrl = config.BaseUrl,
                ApiKey = config.ApiKey,
                Model = config.TranslationModel
            },
            _ => throw new ArgumentException($"Unknown translation provider: {providerName}", nameof(providerName))
        };
    }

    /// <summary>
    /// Lists available models for the given provider using the IModelListable interface.
    /// </summary>
    public static async Task<List<string>> ListModelsForProvider(string providerName, AppSettings settings, CancellationToken ct = default)
    {
        try
        {
            var provider = CreateOcrProvider(settings, providerName);
            try
            {
                if (provider is IModelListable listable)
                    return await listable.ListModelsAsync(ct);

                if (provider is OllamaOCRProvider ollamaOcr)
                    return await ollamaOcr.ListModelsAsync(ct);

                return new List<string>();
            }
            finally
            {
                (provider as IDisposable)?.Dispose();
            }
        }
        catch
        {
            return new List<string>();
        }
    }
}
