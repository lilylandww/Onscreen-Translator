using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace WpfAppTest;

/// <summary>
/// MangaOCR wrapper that uses Ollama's vision model directly via HTTP API.
/// This replaces the previous Python service approach with direct Ollama API calls for better performance.
/// </summary>
public class MangaOCR : IDisposable
{
    private static MangaOCR? _instance;
    private static readonly object _lock = new();

    private HttpClient? _httpClient;
    private bool _disposed;

    /// <summary>
    /// Configuration for the OCR service
    /// </summary>
    public OllamaOCRConfig Config { get; set; } = new();

    private MangaOCR()
    {
        // Private constructor for singleton pattern
    }

    /// <summary>
    /// Configuration for Ollama OCR
    /// </summary>
    public class OllamaOCRConfig
    {
        public string Host { get; set; } = "127.0.0.1";
        public int Port { get; set; } = 11434;
        public string Model { get; set; } = "glm-ocr:q8_0";
        public int TimeoutSeconds { get; set; } = 120;
        public int MaxRetries { get; set; } = 3;
        public int RetryDelayMs { get; set; } = 1000;
        public string Prompt { get; set; } = "Extract all text from this image. Return only the text, no explanations or commentary.";
        /// <summary>
        /// Generation options sent with every OCR request. These cap the output
        /// length and break the degenerate fence/paragraph repetition that some
        /// vision models (notably glm-ocr) fall into, which otherwise wastes
        /// 15+ seconds per image on garbage tokens.
        /// </summary>
        public OllamaGenerationOptions Generation { get; set; } = new();
    }

    /// <summary>
    /// Ollama generation options that cap output and stop degenerate repetition.
    /// </summary>
    public class OllamaGenerationOptions
    {
        public int NumPredict { get; set; } = 256;
        public double Temperature { get; set; } = 0.0;
        public double RepeatPenalty { get; set; } = 1.2;
        public List<string> Stop { get; set; } = new() { "```", "``" };
    }

    /// <summary>
    /// Ollama generate API request payload
    /// </summary>
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

    /// <summary>
    /// Ollama /api/generate options block. Property names use the snake_case
    /// form expected by Ollama's API.
    /// </summary>
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

    /// <summary>
    /// Ollama generate API response
    /// </summary>
    private class OllamaGenerateResponse
    {
        [JsonPropertyName("model")]
        public string? Model { get; set; }

        [JsonPropertyName("response")]
        public string? Response { get; set; }

        [JsonPropertyName("done")]
        public bool Done { get; set; }
    }

    /// <summary>
    /// Ollama tags API response for listing models
    /// </summary>
    private class OllamaTagsResponse
    {
        [JsonPropertyName("models")]
        public List<OllamaModel>? Models { get; set; }
    }

    private class OllamaModel
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("model")]
        public string? ModelId { get; set; }
    }

    /// <summary>
    /// Gets the singleton instance of MangaOCR
    /// </summary>
    public static MangaOCR Instance
    {
        get
        {
            if (_instance == null)
            {
                lock (_lock)
                {
                    _instance ??= new MangaOCR();
                }
            }
            return _instance;
        }
    }

    /// <summary>
    /// Initializes the OCR client with the specified configuration
    /// </summary>
    /// <param name="config">Optional configuration. Uses defaults if not provided.</param>
    public void Initialize(OllamaOCRConfig? config = null)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(MangaOCR));
        }

        lock (_lock)
        {
            if (_httpClient != null)
            {
                Console.WriteLine("MangaOCR client already initialized");
                return;
            }

            Config = config ?? new OllamaOCRConfig();
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(Config.TimeoutSeconds),
                BaseAddress = new Uri($"http://{Config.Host}:{Config.Port}")
            };

            Console.WriteLine($"MangaOCR initialized with Ollama: host={Config.Host}, port={Config.Port}, model={Config.Model}");
        }
    }

    /// <summary>
    /// Lists available Ollama models
    /// </summary>
    /// <returns>List of model names</returns>
    public async Task<List<string>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        EnsureInitialized();

        if (_httpClient == null)
        {
            throw new InvalidOperationException("OCR client not initialized");
        }

        try
        {
            var response = await _httpClient.GetAsync("/api/tags", cancellationToken);
            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var tagsResponse = JsonSerializer.Deserialize<OllamaTagsResponse>(content);

            return tagsResponse?.Models?.Select(m => m.Name ?? m.ModelId ?? "unknown").Where(n => n != null).ToList() ?? new List<string>();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to list Ollama models: {ex.Message}");
            throw new InvalidOperationException($"Failed to list Ollama models: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Finds the best available OCR model from Ollama
    /// Prioritizes glm-ocr models, falls back to vision-capable models
    /// </summary>
    /// <returns>The best available model name, or null if no suitable model found</returns>
    public async Task<string?> FindBestModelAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var models = await ListModelsAsync(cancellationToken);

            // Priority order for OCR models
            var priorityModels = new[]
            {
                "glm-ocr",
                "llava",
                "bakllava",
                "moondream",
                "qwen2.5vl",
                "gemma3"
            };

            foreach (var priority in priorityModels)
            {
                var match = models.FirstOrDefault(m => m.Contains(priority, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrEmpty(match))
                {
                    return match;
                }
            }

            // If no priority model found, return the first available
            return models.FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Gets text from an image file using Ollama's vision model
    /// </summary>
    /// <param name="imagePath">Path to the image file</param>
    /// <returns>Extracted text from the image</returns>
    public string GetTextFromOCR(string imagePath)
    {
        // Use Task.Run to avoid blocking the UI thread and prevent deadlock
        return Task.Run(async () => await GetTextFromOCRAsync(imagePath)).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Async version of GetTextFromOCR
    /// </summary>
    /// <param name="imagePath">Path to the image file</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Extracted text from the image</returns>
    public async Task<string> GetTextFromOCRAsync(string imagePath, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();

        if (_httpClient == null)
        {
            throw new InvalidOperationException("OCR client not initialized");
        }

        if (!File.Exists(imagePath))
        {
            throw new FileNotFoundException($"Image file not found: {imagePath}", imagePath);
        }

        // Process image: load, convert to RGB, save as PNG, then base64 encode
        string base64Image = await Task.Run(() => ProcessImageToBase64(imagePath), cancellationToken);

        return await ProcessImageWithOllamaAsync(base64Image, cancellationToken);
    }

    /// <summary>
    /// Processes an image file: loads it, converts to RGB if needed, saves as PNG, and returns base64
    /// </summary>
    /// <param name="imagePath">Path to the image file</param>
    /// <returns>Base64-encoded PNG image</returns>
    private string ProcessImageToBase64(string imagePath)
    {
        Debug.WriteLine($"[MangaOCR] Processing image: {imagePath}");
        Debug.WriteLine($"[MangaOCR] File exists: {File.Exists(imagePath)}");
        Debug.WriteLine($"[MangaOCR] File size: {new FileInfo(imagePath).Length} bytes");

        using var originalImage = Image.FromFile(imagePath);
        Debug.WriteLine($"[MangaOCR] Image size: {originalImage.Width}x{originalImage.Height}, Format: {originalImage.PixelFormat}");

        // Convert to RGB format if needed
        Bitmap rgbImage;
        if (originalImage.PixelFormat != PixelFormat.Format24bppRgb &&
            originalImage.PixelFormat != PixelFormat.Format32bppRgb)
        {
            rgbImage = new Bitmap(originalImage.Width, originalImage.Height, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(rgbImage))
            {
                g.DrawImage(originalImage, 0, 0, originalImage.Width, originalImage.Height);
            }
            Debug.WriteLine($"[MangaOCR] Converted to RGB24");
        }
        else
        {
            rgbImage = new Bitmap(originalImage);
            Debug.WriteLine($"[MangaOCR] Using original format");
        }

        using (rgbImage)
        {
            using var memoryStream = new MemoryStream();
            rgbImage.Save(memoryStream, ImageFormat.Png);
            byte[] imageBytes = memoryStream.ToArray();
            string base64 = Convert.ToBase64String(imageBytes);
            Debug.WriteLine($"[MangaOCR] Base64 length: {base64.Length} chars");
            return base64;
        }
    }

    /// <summary>
    /// Processes an image with Ollama's vision model
    /// </summary>
    /// <param name="base64Image">Base64-encoded image</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Extracted text</returns>
    private async Task<string> ProcessImageWithOllamaAsync(string base64Image, CancellationToken cancellationToken)
    {
        string activePrompt = Config.Prompt;
        if (!string.IsNullOrEmpty(Config.Model) && Config.Model.Contains("glm-ocr", StringComparison.OrdinalIgnoreCase))
        {
            activePrompt = "Text Recognition:";
        }

        var payload = new OllamaGenerateRequest
        {
            Model = Config.Model,
            Prompt = activePrompt,
            Images = new List<string> { base64Image },
            Stream = false,
            Options = new OllamaOptionsPayload
            {
                NumPredict = Config.Generation.NumPredict,
                Temperature = Config.Generation.Temperature,
                RepeatPenalty = Config.Generation.RepeatPenalty,
                Stop = Config.Generation.Stop
            }
        };

        var jsonPayload = JsonSerializer.Serialize(payload);
        var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

        // Retry logic for transient failures
        for (int attempt = 1; attempt <= Config.MaxRetries; attempt++)
        {
            try
            {
                Debug.WriteLine($"OCR attempt {attempt}/{Config.MaxRetries} with model {Config.Model}");

                var response = await _httpClient!.PostAsync("/api/generate", content, cancellationToken);
                var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException($"Ollama API returned status {response.StatusCode}: {responseContent}");
                }

                var result = JsonSerializer.Deserialize<OllamaGenerateResponse>(responseContent);

                if (result == null)
                {
                    throw new InvalidOperationException("Failed to deserialize Ollama response");
                }

                var extractedText = result.Response?.Trim() ?? string.Empty;
                var cleanedText = CleanOcrOutput(extractedText);
                Debug.WriteLine($"OCR successful. Extracted text length: {cleanedText.Length}");

                return cleanedText;
            }
            catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (attempt < Config.MaxRetries)
            {
                Debug.WriteLine($"OCR attempt {attempt} failed: {ex.Message}. Retrying...");
                await Task.Delay(Config.RetryDelayMs, cancellationToken);
            }
        }

        throw new InvalidOperationException($"OCR failed after {Config.MaxRetries} attempts");
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

    /// <summary>
    /// Gets text from custom OCR (alias for GetTextFromOCR for backward compatibility)
    /// </summary>
    /// <param name="imagePath">Path to the image file</param>
    /// <returns>Extracted text from the image</returns>
    public string GetTextFromCustomOCR(string imagePath)
    {
        return GetTextFromOCR(imagePath);
    }

    /// <summary>
    /// Async version of GetTextFromCustomOCR
    /// </summary>
    /// <param name="imagePath">Path to the image file</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Extracted text from the image</returns>
    public Task<string> GetTextFromCustomOCRAsync(string imagePath, CancellationToken cancellationToken = default)
    {
        return GetTextFromOCRAsync(imagePath, cancellationToken);
    }

    /// <summary>
    /// Checks if the Ollama service is healthy and available
    /// </summary>
    /// <returns>True if service is healthy</returns>
    public async Task<bool> IsHealthyAsync()
    {
        try
        {
            EnsureInitialized();

            if (_httpClient == null)
            {
                return false;
            }

            var response = await _httpClient.GetAsync("/api/tags");
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Health check failed: {ex.Message}");
            return false;
        }
    }

    private void EnsureInitialized()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(MangaOCR));
        }

        if (_httpClient == null)
        {
            Initialize();
        }
    }

    /// <summary>
    /// Disposes the OCR client
    /// </summary>
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

    /// <summary>
    /// Cleanup method for backward compatibility
    /// </summary>
    public static void CleanUp()
    {
        _instance?.Dispose();
        _instance = null;
    }
}
