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
    /// <summary>Max tokens to generate per OCR call. Caps runaway output.</summary>
    public int NumPredict { get; set; } = 256;
    /// <summary>Sampling temperature. 0 = greedy/deterministic.</summary>
    public double Temperature { get; set; } = 0.0;
    /// <summary>Penalty applied to repeated tokens to break degenerate loops.</summary>
    public double RepeatPenalty { get; set; } = 1.2;
    /// <summary>Stop sequences that halt generation early (e.g. markdown fences).</summary>
    public List<string> StopSequences { get; set; } = new() { "```", "``" };

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
        [JsonPropertyName("options")]
        public OllamaOptionsPayload? Options { get; set; }
    }

    private class OllamaOptionsPayload
    {
        [JsonPropertyName("num_predict")]
        public int NumPredict { get; set; }
        [JsonPropertyName("temperature")]
        public double Temperature { get; set; }
        [JsonPropertyName("repeat_penalty")]
        public double RepeatPenalty { get; set; }
        [JsonPropertyName("stop")]
        public List<string> Stop { get; set; } = new();
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

        // GLM-OCR expects a specific task-prefix prompt rather than general instructions
        string activePrompt = Prompt;
        if (!string.IsNullOrEmpty(Model) && Model.Contains("glm-ocr", StringComparison.OrdinalIgnoreCase))
        {
            activePrompt = "Text Recognition:";
        }

        var payload = new OllamaGenerateRequest
        {
            Model = Model,
            Prompt = activePrompt,
            Images = new List<string> { base64Image },
            Stream = false,
            Options = new OllamaOptionsPayload
            {
                NumPredict = NumPredict,
                Temperature = Temperature,
                RepeatPenalty = RepeatPenalty,
                Stop = StopSequences
            }
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
                var text = result?.Response?.Trim() ?? string.Empty;
                Debug.WriteLine($"[OllamaOCR] Raw model response ({text.Length} chars):\n{text}");

                if (IsModelError(text))
                    throw new InvalidOperationException(text);

                var cleaned = CleanOcrOutput(text);
                Debug.WriteLine($"[OllamaOCR] Cleaned output ({cleaned.Length} chars):\n{cleaned}");
                return cleaned;
            }
            catch (TaskCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (InvalidOperationException) { throw; }
            catch (Exception ex) when (attempt < MaxRetries)
            {
                Debug.WriteLine($"[OllamaOCR] Attempt {attempt} failed: {ex.Message}");
                await Task.Delay(RetryDelayMs, ct);
            }
        }

        throw new InvalidOperationException($"OCR failed after {MaxRetries} attempts");
    }

    /// <summary>
    /// Returns true if the model response is an error message rather than valid OCR output.
    /// Catches common errors like models that don't support image input.
    /// </summary>
    private static bool IsModelError(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        var lower = text.ToLowerInvariant();
        string[] errorPatterns =
        [
            "does not support image",
            "cannot read",
            "this model does not support",
            "no vision support",
            "image input is not supported",
            "multimodal input is not supported",
        ];

        return errorPatterns.Any(p => lower.Contains(p));
    }

    /// <summary>
    /// Post-processing harness that cleans up LLM output to ensure it contains
    /// only the extracted text. Handles common LLM tendency to add commentary or markdown fences.
    /// </summary>
    private static string CleanOcrOutput(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

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
                if (trimmed.StartsWith("```", StringComparison.Ordinal))
                {
                    continue; // Skip code fence markers
                }
                var sentenceEnd = trimmed.IndexOfAny(['.', ':', '。']);
                if (sentenceEnd > 0 && sentenceEnd < trimmed.Length - 1)
                {
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

            if (trimmed.StartsWith("```", StringComparison.Ordinal))
                continue;

            started = true;
            cleanLines.Add(line);
        }

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

        return DeduplicateOcrOutput(string.Join("\n", cleanLines)).Trim();
    }

    /// <summary>
    /// Collapses repeated OCR output. Some vision models (notably glm-ocr via
    /// Ollama's /api/generate endpoint) emit the recognized text more than once,
    /// usually as two identical paragraphs separated by a blank line. This keeps
    /// a single copy. Only fires when the output is clearly doubled — genuinely
    /// distinct paragraphs are preserved.
    /// </summary>
    private static string DeduplicateOcrOutput(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var paragraphs = normalized.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
                                   .Select(p => p.Trim())
                                   .Where(p => p.Length > 0)
                                   .ToList();

        if (paragraphs.Count < 2) return text;

        List<string> deduped = paragraphs;

        // All paragraphs identical -> keep a single copy
        if (paragraphs.All(p => string.Equals(p, paragraphs[0], StringComparison.Ordinal)))
        {
            deduped = new List<string> { paragraphs[0] };
        }
        // Even count where the second half repeats the first -> keep first half
        else if (paragraphs.Count % 2 == 0)
        {
            int half = paragraphs.Count / 2;
            bool halvesMatch = true;
            for (int i = 0; i < half; i++)
            {
                if (!string.Equals(paragraphs[i], paragraphs[i + half], StringComparison.Ordinal))
                {
                    halvesMatch = false;
                    break;
                }
            }
            if (halvesMatch)
                deduped = paragraphs.Take(half).ToList();
        }

        return deduped.Count == paragraphs.Count ? text : string.Join("\n\n", deduped);
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
