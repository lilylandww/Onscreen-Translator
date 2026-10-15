using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WpfAppTest.Providers;

/// <summary>
/// OCR provider using Google's Gemini generateContent API with vision
/// (e.g., gemini-2.0-flash/flash-lite). Images are sent inline as base64 PNG.
/// Shares the OCR system prompt and output-cleaning harness with
/// <see cref="OpenAIOCRProvider"/>.
/// </summary>
public class GeminiOCRProvider : IOCRProvider, IModelListable, IDisposable
{
    private HttpClient? _httpClient;
    private bool _disposed;

    public string Name => "gemini";
    public string DisplayName => $"Google Gemini ({Model})";

    private string _baseUrl = "https://generativelanguage.googleapis.com/v1beta";
    public string BaseUrl
    {
        get => _baseUrl;
        set
        {
            _baseUrl = value.TrimEnd('/');
            if (_httpClient != null)
                _httpClient.BaseAddress = new Uri(_baseUrl + "/");
        }
    }

    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "gemini-2.0-flash";
    public int TimeoutSeconds { get; set; } = 120;

    public string SystemPrompt { get; set; } = OpenAIOCRDefaults.SystemPrompt;

    public string UserPrompt { get; set; } = OpenAIOCRDefaults.UserPrompt;

    public GeminiOCRProvider()
    {
    }

    private HttpClient GetClient()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_httpClient != null) return _httpClient;

        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(TimeoutSeconds),
            BaseAddress = new Uri(_baseUrl + "/")
        };

        if (!string.IsNullOrEmpty(ApiKey))
        {
            _httpClient.DefaultRequestHeaders.Add("x-goog-api-key", ApiKey);
        }

        return _httpClient;
    }

    public async Task<string> RecognizeTextAsync(string imagePath, CancellationToken ct = default)
    {
        if (!File.Exists(imagePath))
            throw new FileNotFoundException($"Image not found: {imagePath}", imagePath);

        string base64Image = await Task.Run(() => ProcessImageToBase64(imagePath), ct);

        var request = new GeminiRequest
        {
            SystemInstruction = new GeminiContent
            {
                Parts = [new GeminiPart { Text = SystemPrompt }]
            },
            Contents =
            [
                new GeminiContent
                {
                    Role = "user",
                    Parts =
                    [
                        new GeminiPart { Text = UserPrompt },
                        new GeminiPart
                        {
                            InlineData = new GeminiInlineData { Data = base64Image }
                        }
                    ]
                }
            ],
            GenerationConfig = new GeminiGenerationConfig { Temperature = 0.0, MaxOutputTokens = 8192 }
        };

        var json = JsonSerializer.Serialize(request);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var client = GetClient();
        var response = await client.PostAsync($"models/{Model}:generateContent", content, ct);
        var responseBody = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"OCR API error ({(int)response.StatusCode}): {responseBody}");

        var result = JsonSerializer.Deserialize<GeminiResponse>(responseBody);
        var extractedText = result?.Candidates?.FirstOrDefault()?.Content?.Parts?
            .Select(p => p.Text)
            .FirstOrDefault(t => !string.IsNullOrWhiteSpace(t))
            ?? string.Empty;

        return OcrOutputCleaner.Clean(extractedText.Trim());
    }

    public async Task<bool> IsAvailableAsync()
    {
        try
        {
            var client = GetClient();
            var response = await client.GetAsync("models");
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public async Task<List<string>> ListModelsAsync(CancellationToken ct = default)
    {
        // Gemini lists models with the same endpoint/auth for OCR and translation
        using var provider = new GeminiTranslationProvider
        {
            BaseUrl = BaseUrl,
            ApiKey = ApiKey,
            TimeoutSeconds = TimeoutSeconds
        };
        return await provider.ListModelsAsync(ct);
    }

    private static string ProcessImageToBase64(string imagePath)
    {
        using var original = Image.FromFile(imagePath);
        using var rgbImage = ToRgbBitmap(original);
        using var ms = new MemoryStream();
        rgbImage.Save(ms, ImageFormat.Png);
        return Convert.ToBase64String(ms.ToArray());
    }

    private static Bitmap ToRgbBitmap(Image original)
    {
        if (original.PixelFormat == PixelFormat.Format24bppRgb ||
            original.PixelFormat == PixelFormat.Format32bppRgb)
        {
            return new Bitmap(original);
        }

        var bmp = new Bitmap(original.Width, original.Height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(bmp))
            g.DrawImage(original, 0, 0, original.Width, original.Height);
        return bmp;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _httpClient?.Dispose();
            _httpClient = null;
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Shared vision-OCR prompt defaults so OpenAI/Gemini/Anthropic OCR providers
/// behave identically.
/// </summary>
internal static class OpenAIOCRDefaults
{
    public const string SystemPrompt = @"You are a precision OCR text extraction engine. Your sole purpose is to extract ALL text from the provided image with perfect fidelity.

RULES — follow these without exception:
1. Transcribe text EXACTLY as it appears in the image. Do NOT correct, translate, or interpret.
2. Preserve the original reading order:
   - Horizontal text: left-to-right, top-to-bottom
   - Vertical text: top-to-bottom, right-to-left (common in Japanese manga)
3. Maintain all line breaks, spacing, and punctuation as shown in the image.
4. If text appears in speech bubbles or boxes, extract only the text inside them, in order.
5. Do NOT add any commentary, explanations, labels, formatting markers, or metadata.
6. Do NOT add content that is not visible in the image.
7. If multiple distinct text regions exist, concatenate them in reading order separated by newlines.
8. Output ONLY the raw transcribed text. Nothing else.

This is critical: your entire output will be used directly as OCR text. Any deviation from the source image is an error.";

    public const string UserPrompt = "Extract all text from this image. Return only the transcribed text, exactly as it appears.";
}