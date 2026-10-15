using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using WpfAppTest.Providers.Breakdown;

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
    public static readonly string[] AvailableOcrProviders = ["ollama", "openai", "openai-compatible", "gemini", "anthropic"];

    /// <summary>
    /// All available translation provider names.
    /// </summary>
    public static readonly string[] AvailableTranslationProviders = ["google", "deepl", "ollama", "openai", "openai-compatible", "gemini", "anthropic"];

    /// <summary>
    /// Providers able to run the sentence-breakdown LLM call (LLM endpoints only —
    /// Google/DeepL cannot produce structured breakdowns).
    /// </summary>
    public static readonly string[] AvailableBreakdownProviders = ["ollama", "openai", "openai-compatible", "gemini", "anthropic"];

    /// <summary>
    /// Display names for OCR providers.
    /// </summary>
    public static readonly Dictionary<string, string> OcrProviderDisplayNames = new()
    {
        ["ollama"] = "Ollama (Local)",
        ["openai"] = "OpenAI",
        ["openai-compatible"] = "OpenAI-Compatible",
        ["gemini"] = "Google Gemini",
        ["anthropic"] = "Anthropic Claude"
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
        ["openai-compatible"] = "OpenAI-Compatible",
        ["gemini"] = "Google Gemini",
        ["anthropic"] = "Anthropic Claude"
    };

    /// <summary>
    /// Display names for breakdown providers.
    /// </summary>
    public static readonly Dictionary<string, string> BreakdownProviderDisplayNames = new()
    {
        ["ollama"] = "Ollama (Local)",
        ["openai"] = "OpenAI",
        ["openai-compatible"] = "OpenAI-Compatible",
        ["gemini"] = "Google Gemini",
        ["anthropic"] = "Anthropic Claude"
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
            "gemini" => new GeminiOCRProvider
            {
                BaseUrl = string.IsNullOrEmpty(config.BaseUrl) ? "https://generativelanguage.googleapis.com/v1beta" : config.BaseUrl,
                ApiKey = config.ApiKey,
                Model = config.OcrModel
            },
            "anthropic" => new AnthropicOCRProvider
            {
                BaseUrl = string.IsNullOrEmpty(config.BaseUrl) ? "https://api.anthropic.com" : config.BaseUrl,
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

        // Google Gemini
        var geminiConfig = settings.GetProviderConfig("gemini");
        providers.Add(new GeminiTranslationProvider
        {
            BaseUrl = string.IsNullOrEmpty(geminiConfig.BaseUrl) ? "https://generativelanguage.googleapis.com/v1beta" : geminiConfig.BaseUrl,
            ApiKey = geminiConfig.ApiKey,
            Model = geminiConfig.TranslationModel
        });

        // Anthropic Claude
        var anthropicConfig = settings.GetProviderConfig("anthropic");
        providers.Add(new AnthropicTranslationProvider
        {
            BaseUrl = string.IsNullOrEmpty(anthropicConfig.BaseUrl) ? "https://api.anthropic.com" : anthropicConfig.BaseUrl,
            ApiKey = anthropicConfig.ApiKey,
            Model = anthropicConfig.TranslationModel
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
            "gemini" => new GeminiTranslationProvider
            {
                BaseUrl = string.IsNullOrEmpty(config.BaseUrl) ? "https://generativelanguage.googleapis.com/v1beta" : config.BaseUrl,
                ApiKey = config.ApiKey,
                Model = config.TranslationModel
            },
            "anthropic" => new AnthropicTranslationProvider
            {
                BaseUrl = string.IsNullOrEmpty(config.BaseUrl) ? "https://api.anthropic.com" : config.BaseUrl,
                ApiKey = config.ApiKey,
                Model = config.TranslationModel
            },
            _ => throw new ArgumentException($"Unknown translation provider: {providerName}", nameof(providerName))
        };
    }

    /// <summary>
    /// Ollama's OpenAI-compatible routes live under <c>/v1</c> (its native API is
    /// <c>/api/*</c>), so a plain <c>host:port</c> Ollama URL gets <c>/v1</c>
    /// appended before it is used as a chat-completions base address.
    /// Non-Ollama providers are returned unchanged (their URLs are expected to
    /// already include the version path).
    /// </summary>
    public static string WithOpenAIV1Path(string providerName, string baseUrl)
    {
        if (!string.Equals(providerName, "ollama", StringComparison.OrdinalIgnoreCase))
            return baseUrl;

        var trimmed = baseUrl.TrimEnd('/');
        return trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : trimmed + "/v1";
    }

    /// <summary>
    /// Creates the sentence-breakdown provider from settings. This is the single
    /// place where the breakdown endpoint/model is resolved: uses
    /// <c>Breakdown.Model</c> (falling back to the chosen provider's translation
    /// model when empty), applies Ollama <c>/v1</c> normalization, and defaults a
    /// blank base URL per provider. Returns null when the target endpoint is
    /// unconfigured — callers fall back to plain rendering.
    /// </summary>
    public static ISentenceBreakdownProvider? CreateBreakdownProvider(AppSettings settings)
    {
        var breakdown = settings.Breakdown;
        var providerName = breakdown.Provider;
        if (Array.IndexOf(AvailableBreakdownProviders, providerName) < 0)
            providerName = "ollama";

        var config = settings.GetProviderConfig(providerName);
        var model = string.IsNullOrEmpty(breakdown.Model) ? config.TranslationModel : breakdown.Model;
        var rawBaseUrl = string.IsNullOrEmpty(config.BaseUrl)
            ? DefaultBaseUrl(providerName)
            : config.BaseUrl;
        var baseUrl = WithOpenAIV1Path(providerName, rawBaseUrl);

        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(model))
        {
            Debug.WriteLine("[Breakdown] Cannot create provider — endpoint/model not configured.");
            return null;
        }

        ISentenceBreakdownProvider provider = providerName switch
        {
            "gemini" => new GeminiSentenceBreakdownProvider(baseUrl, config.ApiKey, model),
            "anthropic" => new AnthropicSentenceBreakdownProvider(baseUrl, config.ApiKey, model),
            _ => new LLMSentenceBreakdownProvider(baseUrl, config.ApiKey, model)
        };

        return provider;
    }

    /// <summary>
    /// Default base URL for a provider when its config leaves it blank.
    /// Returns "" for Ollama/OpenAI-compatible (a blank URL means unconfigured).
    /// </summary>
    private static string DefaultBaseUrl(string providerName) => providerName switch
    {
        "openai" => "https://api.openai.com/v1",
        "gemini" => "https://generativelanguage.googleapis.com/v1beta",
        "anthropic" => "https://api.anthropic.com",
        _ => ""
    };

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
