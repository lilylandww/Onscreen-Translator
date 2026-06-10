using System;
using System.Collections.Generic;
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

/// <summary>
/// OCR provider using OpenAI's Chat Completions API with vision (e.g., GPT-4o).
/// Also works as the base for OpenAI-compatible endpoints (LM Studio, vLLM, etc.)
/// Includes a specialized system prompt and harness to ensure accurate text extraction
/// when using a general-purpose LLM for OCR tasks.
/// </summary>
public class OpenAIOCRProvider : IOCRProvider, IModelListable, IDisposable
{
    private HttpClient? _httpClient;
    private bool _disposed;

    public string Name => _providerName;
    public string DisplayName => $"{_displayName} ({Model})";

    private readonly string _providerName;
    private readonly string _displayName;

    private string _baseUrl = "https://api.openai.com/v1";
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

    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "gpt-4o";
    public int TimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// Specialized system prompt for LLM-based OCR that ensures faithful text extraction.
    /// This prompt constrains the LLM to act purely as a text extractor without hallucination.
    /// </summary>
    public string SystemPrompt { get; set; } = @"You are a precision OCR text extraction engine. Your sole purpose is to extract ALL text from the provided image with perfect fidelity.

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

    public string UserPrompt { get; set; } = "Extract all text from this image. Return only the transcribed text, exactly as it appears.";

    public OpenAIOCRProvider(string providerName = "openai", string displayName = "OpenAI")
    {
        _providerName = providerName;
        _displayName = displayName;
    }

    #region OpenAI API DTOs (OCR-specific)

    private class TextContent
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "text";

        [JsonPropertyName("text")]
        public string Text { get; set; } = "";
    }

    private class ImageContent
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "image_url";

        [JsonPropertyName("image_url")]
        public ImageUrl ImageUrl { get; set; } = new();
    }

    private class ImageUrl
    {
        [JsonPropertyName("url")]
        public string Url { get; set; } = "";

        [JsonPropertyName("detail")]
        public string Detail { get; set; } = "high";
    }

    #endregion

    private HttpClient GetClient()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_httpClient != null) return _httpClient;

        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(TimeoutSeconds),
            BaseAddress = new Uri(_baseUrl)
        };

        if (!string.IsNullOrEmpty(ApiKey))
        {
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {ApiKey}");
        }

        return _httpClient;
    }

    public async Task<string> RecognizeTextAsync(string imagePath, CancellationToken ct = default)
    {
        if (!File.Exists(imagePath))
            throw new FileNotFoundException($"Image not found: {imagePath}", imagePath);

        string base64Image = await Task.Run(() => ProcessImageToBase64(imagePath), ct);
        string dataUri = $"data:image/png;base64,{base64Image}";

        var request = new ChatRequest
        {
            Model = Model,
            Messages = new List<ChatMessage>
            {
                new()
                {
                    Role = "system",
                    Content = SystemPrompt
                },
                new()
                {
                    Role = "user",
                    Content = new List<object>
                    {
                        new TextContent { Text = UserPrompt },
                        new ImageContent
                        {
                            ImageUrl = new ImageUrl { Url = dataUri, Detail = "high" }
                        }
                    }
                }
            }
        };

        var json = JsonSerializer.Serialize(request);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var client = GetClient();
        var response = await client.PostAsync("/chat/completions", content, ct);
        var responseBody = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"OCR API error ({(int)response.StatusCode}): {responseBody}");

        var result = JsonSerializer.Deserialize<ChatResponse>(responseBody);
        var extractedText = result?.Choices?
            .FirstOrDefault()?.Message?.Content?
            .Trim() ?? string.Empty;

        // Post-processing harness: clean up any residual non-OCR output
        return CleanOcrOutput(extractedText);
    }

    /// <summary>
    /// Post-processing harness that cleans up LLM output to ensure it contains
    /// only the extracted text. Handles common LLM tendency to add commentary.
    /// </summary>
    private static string CleanOcrOutput(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        // Remove common LLM preamble patterns
        var lines = text.Split('\n');
        var cleanLines = new List<string>();
        bool started = false;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();

            // Skip common preamble patterns
            if (!started && (
                trimmed.StartsWith("Here", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("The text", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("I can see", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("The image", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("From the image", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("In the image", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("Note:", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("Sure,", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("Certainly,", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("```", StringComparison.Ordinal)))
            {
                // Skip preamble line
                if (trimmed.StartsWith("```", StringComparison.Ordinal))
                {
                    // Skip code fence markers entirely
                    continue;
                }
                // Check if this line might be a preamble followed by actual text
                // Only skip if it looks like a sentence (contains period or colon before actual content)
                var sentenceEnd = trimmed.IndexOfAny(['.', ':', '。']);
                if (sentenceEnd > 0 && sentenceEnd < trimmed.Length - 1)
                {
                    // This is likely preamble, but the text after might be actual content
                    // Skip just the preamble part
                    var afterPreamble = trimmed[(sentenceEnd + 1)..].Trim();
                    if (!string.IsNullOrEmpty(afterPreamble))
                    {
                        cleanLines.Add(afterPreamble);
                        started = true;
                    }
                    continue;
                }
                continue;
            }

            // Remove trailing code fences
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
                continue;

            started = true;
            cleanLines.Add(line);
        }

        // Remove trailing common postamble patterns
        while (cleanLines.Count > 0)
        {
            var lastLine = cleanLines[^1].Trim();
            if (lastLine.StartsWith("Note:", StringComparison.OrdinalIgnoreCase) ||
                lastLine.StartsWith("Hope this", StringComparison.OrdinalIgnoreCase) ||
                lastLine.StartsWith("Let me know", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrEmpty(lastLine))
            {
                cleanLines.RemoveAt(cleanLines.Count - 1);
            }
            else
            {
                break;
            }
        }

        return string.Join("\n", cleanLines).Trim();
    }

    public async Task<bool> IsAvailableAsync()
    {
        try
        {
            var client = GetClient();
            var response = await client.GetAsync("/models");
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public async Task<List<string>> ListModelsAsync(CancellationToken ct = default)
    {
        try
        {
            var client = GetClient();
            var response = await client.GetAsync("/models", ct);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync(ct);
            var models = JsonSerializer.Deserialize<ModelsResponse>(body);
            return models?.Data?
                .Select(m => m.Id ?? "unknown")
                .Where(n => n != "unknown")
                .OrderBy(n => n)
                .ToList() ?? new List<string>();
        }
        catch
        {
            return new List<string>();
        }
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
