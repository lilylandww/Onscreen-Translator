using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace WpfAppTest.Providers.Furigana;

/// <summary>
/// Degradation-path fallback provider that asks an OpenAI-compatible LLM
/// (e.g. Ollama) to produce furigana ruby output when the sidecar's FLFL
/// model is unavailable or too slow.
/// </summary>
public class OllamaFuriganaFallbackProvider : IFuriganaFallbackProvider, IDisposable
{
    private HttpClient? _httpClient;
    private readonly string _baseUrl;
    private readonly string _apiKey;
    private readonly string _model;
    private bool _disposed;

    /// <summary>
    /// Creates a new <see cref="OllamaFuriganaFallbackProvider"/>.
    /// </summary>
    /// <param name="baseUrl">Base URL of the OpenAI-compatible endpoint (e.g. "http://localhost:11434").</param>
    /// <param name="apiKey">API key (may be empty for Ollama).</param>
    /// <param name="model">Model name to use for furigana generation.</param>
    public OllamaFuriganaFallbackProvider(string baseUrl, string apiKey, string model)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _apiKey = apiKey;
        _model = model;
    }

    private HttpClient GetClient()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_httpClient != null) return _httpClient;

        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(60),
            BaseAddress = new Uri(_baseUrl)
        };

        if (!string.IsNullOrEmpty(_apiKey))
        {
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_apiKey}");
        }

        return _httpClient;
    }

    public async Task<List<FuriganaSegment>> GetFallbackAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new List<FuriganaSegment>();

        try
        {
            var prompt = $"次の日本語の文に振り仮名をつけて、<ruby>漢字<rt>かな</rt></ruby>形式で出力してください: {text}";

            var request = new ChatRequest
            {
                Model = _model,
                Messages = new List<ChatMessage>
                {
                    new()
                    {
                        Role = "user",
                        Content = prompt
                    }
                }
            };

            var json = JsonSerializer.Serialize(request);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var client = GetClient();
            var response = await client.PostAsync("/chat/completions", content, ct);
            response.EnsureSuccessStatusCode();

            var responseBody = await response.Content.ReadAsStringAsync(ct);
            var result = JsonSerializer.Deserialize<ChatResponse>(responseBody);
            var rubyOutput = result?.Choices?.FirstOrDefault()?.Message?.Content?.Trim();

            if (string.IsNullOrWhiteSpace(rubyOutput))
                return new List<FuriganaSegment>();

            return ParseRubyOutput(rubyOutput, text);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[OllamaFuriganaFallbackProvider] Error: {ex.Message}");
            return new List<FuriganaSegment>();
        }
    }

    /// <summary>
    /// Parses the LLM's ruby-tagged output into <see cref="FuriganaSegment"/> list.
    /// Handles patterns like <c>&lt;ruby&gt;漢字&lt;rt&gt;かんじ&lt;/rt&gt;&lt;/ruby&gt;</c>.
    /// Strips special tokens, validates alignment against the original text,
    /// and handles trailing-kana duplication in FLFL-style output.
    /// </summary>
    internal static List<FuriganaSegment> ParseRubyOutput(string rubyOutput, string originalText)
    {
        // Step 1: Strip special tokens
        rubyOutput = System.Text.RegularExpressions.Regex.Replace(
            rubyOutput,
            @"<\|endoftext\|>|<\|im_start\|>|<\|im_end\|>|<\|pad\|>",
            "");

        var segments = new List<FuriganaSegment>();
        var rubyPattern = new System.Text.RegularExpressions.Regex(
            @"<ruby>(.+?)<rt>(.*?)</rt></ruby>",
            System.Text.RegularExpressions.RegexOptions.Compiled);

        int lastIndex = 0;
        var matches = rubyPattern.Matches(rubyOutput);

        foreach (System.Text.RegularExpressions.Match match in matches)
        {
            // Emit any plain text before this ruby tag as a segment with null reading
            if (match.Index > lastIndex)
            {
                var plainText = rubyOutput[lastIndex..match.Index];
                if (!string.IsNullOrWhiteSpace(plainText))
                {
                    segments.Add(new FuriganaSegment(plainText, null, "", false));
                }
            }

            var surface = match.Groups[1].Value;
            var reading = match.Groups[2].Value;

            // Handle trailing kana duplication:
            // e.g. <ruby>鰤<rt>ぶり</rt></ruby>ぶり → surface "鰤ぶり", reading "ぶり"
            int afterRuby = match.Index + match.Length;
            string combinedSurface = surface;
            string? combinedReading = string.IsNullOrEmpty(reading) ? null : reading;

            if (afterRuby < rubyOutput.Length)
            {
                int kanaEnd = afterRuby;
                while (kanaEnd < rubyOutput.Length && IsKana(rubyOutput[kanaEnd]))
                    kanaEnd++;

                if (kanaEnd > afterRuby)
                {
                    string trailing = rubyOutput[afterRuby..kanaEnd];

                    // Only merge trailing kana if:
                    // 1. The trailing text exactly continues the reading string, AND
                    // 2. Merging produces a surface that aligns with the original text
                    if (!string.IsNullOrEmpty(reading) && reading.EndsWith(trailing, StringComparison.Ordinal))
                    {
                        // Trailing kana is a suffix of the reading — extend the surface
                        string candidateSurface = surface + trailing;
                        // Check that this candidate surface aligns with the original text
                        string originalNorm = NormalizeWhitespace(originalText);
                        string surfacesSoFar = string.Concat(segments.Select(s => s.Surface)) + candidateSurface;
                        string remainingOutput = rubyOutput[kanaEnd..];
                        // Quick check: does the candidate surface exist in the remaining original text?
                        if (originalNorm.Contains(surfacesSoFar.Replace(" ", ""), StringComparison.Ordinal) ||
                            originalNorm.StartsWith(surfacesSoFar.Replace(" ", ""), StringComparison.Ordinal))
                        {
                            combinedSurface = candidateSurface;
                            combinedReading = reading;
                            lastIndex = kanaEnd;
                        }
                        else
                        {
                            lastIndex = match.Index + match.Length;
                        }
                    }
                    else
                    {
                        // Trailing kana is not a reading suffix — add as separate segment later
                        lastIndex = match.Index + match.Length;
                    }
                }
                else
                {
                    lastIndex = match.Index + match.Length;
                }
            }
            else
            {
                lastIndex = match.Index + match.Length;
            }

            segments.Add(new FuriganaSegment(combinedSurface, combinedReading, "", false));
        }

        // Emit any remaining plain text after the last match
        if (lastIndex < rubyOutput.Length)
        {
            var remaining = rubyOutput[lastIndex..];
            if (!string.IsNullOrWhiteSpace(remaining))
            {
                segments.Add(new FuriganaSegment(remaining, null, "", false));
            }
        }

        // Fallback: if parsing produced nothing useful, emit the original text as a single OOV segment
        if (segments.Count == 0 && !string.IsNullOrWhiteSpace(originalText))
        {
            segments.Add(new FuriganaSegment(originalText, null, "", true));
        }

        // Alignment validation: concatenate all surfaces and compare to original text
        if (segments.Count > 0 && !string.IsNullOrWhiteSpace(originalText))
        {
            string concatenatedSurfaces = string.Concat(segments.Select(s => s.Surface));
            string originalNorm = NormalizeWhitespace(originalText);
            string surfacesNorm = NormalizeWhitespace(concatenatedSurfaces);

            if (!string.Equals(originalNorm, surfacesNorm, StringComparison.Ordinal))
            {
                // Alignment failed — return original text as a single OOV segment
                Debug.WriteLine(
                    $"[OllamaFuriganaFallbackProvider] Alignment validation failed: " +
                    $"expected '{originalNorm}', got '{surfacesNorm}'. Falling back to OOV.");
                return new List<FuriganaSegment>
                {
                    new FuriganaSegment(originalText, null, "", true)
                };
            }
        }

        return segments;
    }

    /// <summary>
    /// Removes all whitespace for alignment comparison.
    /// </summary>
    private static string NormalizeWhitespace(string text)
    {
        return System.Text.RegularExpressions.Regex.Replace(text, @"\s+", "");
    }

    private static bool IsKana(char c)
    {
        return (c >= '\u3040' && c <= '\u309F')  // Hiragana
            || (c >= '\u30A0' && c <= '\u30FF');   // Katakana
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

    // --- OpenAI-compatible chat completion DTOs ---

    private class ChatRequest
    {
        [JsonPropertyName("model")]
        public string Model { get; set; } = "";

        [JsonPropertyName("messages")]
        public List<ChatMessage> Messages { get; set; } = new();

        [JsonPropertyName("stream")]
        public bool Stream { get; set; } = false;

        [JsonPropertyName("max_tokens")]
        public int MaxTokens { get; set; } = 4096;
    }

    private class ChatMessage
    {
        [JsonPropertyName("role")]
        public string Role { get; set; } = "";

        [JsonPropertyName("content")]
        public string Content { get; set; } = "";
    }

    private class ChatResponse
    {
        [JsonPropertyName("choices")]
        public List<ChatChoice>? Choices { get; set; }
    }

    private class ChatChoice
    {
        [JsonPropertyName("message")]
        public ChatMessageContent? Message { get; set; }
    }

    private class ChatMessageContent
    {
        [JsonPropertyName("content")]
        public string? Content { get; set; }
    }
}
