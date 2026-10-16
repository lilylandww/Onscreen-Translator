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
/// OCR provider using Anthropic's Messages API with vision (Claude models).
/// Images are sent as base64 blocks inside the user message. Shares the OCR
/// system prompt and output-cleaning harness with <see cref="OpenAIOCRProvider"/>.
/// </summary>
public class AnthropicOCRProvider : IOCRProvider, IModelListable, IDisposable
{
    private HttpClient? _httpClient;
    private bool _disposed;

    public string Name => "anthropic";
    public string DisplayName => $"Anthropic Claude ({Model})";

    private string _baseUrl = "https://api.anthropic.com";
    public string BaseUrl
    {
        get => _baseUrl;
        set
        {
            _baseUrl = value.TrimEnd('/');
            if (_httpClient != null)
                _httpClient.BaseAddress = new Uri(AnthropicTranslationProvider.NormalizeBaseUrl(_baseUrl) + "/");
        }
    }

    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "claude-sonnet-4-5";
    public int TimeoutSeconds { get; set; } = 120;

    public string SystemPrompt { get; set; } = OpenAIOCRDefaults.SystemPrompt;

    public string UserPrompt { get; set; } = OpenAIOCRDefaults.UserPrompt;

    public AnthropicOCRProvider()
    {
    }

    private HttpClient GetClient()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_httpClient != null) return _httpClient;

        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(TimeoutSeconds),
            BaseAddress = new Uri(AnthropicTranslationProvider.NormalizeBaseUrl(_baseUrl) + "/")
        };

        if (!string.IsNullOrEmpty(ApiKey))
        {
            _httpClient.DefaultRequestHeaders.Add("x-api-key", ApiKey);
        }
        _httpClient.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");

        return _httpClient;
    }

    public async Task<string> RecognizeTextAsync(string imagePath, CancellationToken ct = default)
    {
        if (!File.Exists(imagePath))
            throw new FileNotFoundException($"Image not found: {imagePath}", imagePath);

        string base64Image = await Task.Run(() => ProcessImageToBase64(imagePath), ct);

        var request = new AnthropicRequest
        {
            Model = Model,
            MaxTokens = 8192,
            Temperature = 0.0,
            System = SystemPrompt,
            Messages =
            [
                new AnthropicMessage
                {
                    Role = "user",
                    Content =
                    [
                        new AnthropicContentBlock
                        {
                            Type = "image",
                            Source = new AnthropicImageSource { Data = base64Image }
                        },
                        new AnthropicContentBlock { Text = UserPrompt }
                    ]
                }
            ]
        };

        var json = JsonSerializer.Serialize(request);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var client = GetClient();
        var response = await client.PostAsync("messages", content, ct);
        var responseBody = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"OCR API error ({(int)response.StatusCode}): {responseBody}");

        var result = JsonSerializer.Deserialize<AnthropicResponse>(responseBody);
        var extractedText = string.Join("", result?.Content?
            .Where(b => b.Type == "text")
            .Select(b => b.Text ?? string.Empty) ?? Enumerable.Empty<string>());

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
        using var provider = new AnthropicTranslationProvider
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