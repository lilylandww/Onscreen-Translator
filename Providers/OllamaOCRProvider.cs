using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace WpfAppTest.Providers;

public class OllamaOCRProvider : IOCRProvider, IDisposable
{
    private HttpClient? _httpClient;
    private bool _disposed;

    public string Name => "ollama";
    public string DisplayName => $"Ollama ({Model})";

    private string _baseUrl = "http://localhost:11434";
    public string BaseUrl
    {
        get => _baseUrl;
        set
        {
            _baseUrl = value;
            if (_httpClient != null)
                _httpClient.BaseAddress = new Uri(_baseUrl);
        }
    }
    public string Model { get; set; } = "glm-ocr:q8_0";
    public string Prompt { get; set; } = "Extract all text from this image. Return only the text, no explanations or commentary.";
    public int TimeoutSeconds { get; set; } = 120;
    public int MaxRetries { get; set; } = 3;
    public int RetryDelayMs { get; set; } = 1000;

    private class OllamaGenerateRequest
    {
        [JsonPropertyName("model")]
        public string Model { get; set; } = string.Empty;
        [JsonPropertyName("prompt")]
        public string Prompt { get; set; } = string.Empty;
        [JsonPropertyName("images")]
        public List<string> Images { get; set; } = new();
        [JsonPropertyName("stream")]
        public bool Stream { get; set; } = false;
    }

    private class OllamaGenerateResponse
    {
        [JsonPropertyName("response")]
        public string? Response { get; set; }
    }

    private class OllamaTagsResponse
    {
        [JsonPropertyName("models")]
        public List<OllamaModelEntry>? Models { get; set; }
    }

    private class OllamaModelEntry
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }

    private HttpClient GetClient()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _httpClient ??= new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(TimeoutSeconds),
            BaseAddress = new Uri(_baseUrl)
        };
    }

    public async Task<List<string>> ListModelsAsync(CancellationToken ct = default)
    {
        var client = GetClient();
        var response = await client.GetAsync("/api/tags", ct);
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync(ct);
        var tags = JsonSerializer.Deserialize<OllamaTagsResponse>(content);
        return tags?.Models?.Select(m => m.Name ?? "unknown").Where(n => n != "unknown").ToList() ?? new List<string>();
    }

    public async Task<string> RecognizeTextAsync(string imagePath, CancellationToken ct = default)
    {
        if (!File.Exists(imagePath))
            throw new FileNotFoundException($"Image not found: {imagePath}", imagePath);

        string base64Image = await Task.Run(() => ProcessImageToBase64(imagePath), ct);

        var payload = new OllamaGenerateRequest
        {
            Model = Model,
            Prompt = Prompt,
            Images = new List<string> { base64Image },
            Stream = false
        };

        var json = JsonSerializer.Serialize(payload);

        for (int attempt = 1; attempt <= MaxRetries; attempt++)
        {
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            try
            {
                Debug.WriteLine($"[OllamaOCR] Attempt {attempt}/{MaxRetries}, model={Model}");
                var client = GetClient();
                var response = await client.PostAsync("/api/generate", content, ct);
                var responseBody = await response.Content.ReadAsStringAsync(ct);
                response.EnsureSuccessStatusCode();

                var result = JsonSerializer.Deserialize<OllamaGenerateResponse>(responseBody);
                return result?.Response?.Trim() ?? string.Empty;
            }
            catch (TaskCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (attempt < MaxRetries)
            {
                Debug.WriteLine($"[OllamaOCR] Attempt {attempt} failed: {ex.Message}");
                await Task.Delay(RetryDelayMs, ct);
            }
        }

        throw new InvalidOperationException($"OCR failed after {MaxRetries} attempts");
    }

    public async Task<bool> IsAvailableAsync()
    {
        try
        {
            var client = GetClient();
            var response = await client.GetAsync("/api/tags");
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    private string ProcessImageToBase64(string imagePath)
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
