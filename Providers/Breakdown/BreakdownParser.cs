using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace WpfAppTest.Providers.Breakdown;

/// <summary>
/// Pure parser for the breakdown LLM's JSON-array output.
/// Extracts the array (tolerating markdown fences/preambles), deserializes the
/// short-keyed parts, and validates that the surfaces reproduce the original
/// sentence exactly (whitespace-insensitive), mirroring the alignment contract
/// of <c>OllamaFuriganaFallbackProvider.ParseRubyOutput</c>.
/// Returns an empty list on any validation or parse failure.
/// </summary>
internal static class BreakdownParser
{
    private sealed class RawPart
    {
        [JsonPropertyName("s")]
        public string? Surface { get; set; }

        [JsonPropertyName("r")]
        public string? Reading { get; set; }

        [JsonPropertyName("m")]
        public string? Meaning { get; set; }

        [JsonPropertyName("t")]
        public string? Kind { get; set; }
    }

    /// <summary>
    /// Parses LLM output into <see cref="BreakdownPart"/>s aligned to <paramref name="originalText"/>.
    /// Returns an empty list when the output is unusable or misaligned.
    /// </summary>
    internal static List<BreakdownPart> Parse(string llmOutput, string originalText)
    {
        if (string.IsNullOrWhiteSpace(llmOutput) || string.IsNullOrWhiteSpace(originalText))
            return new List<BreakdownPart>();

        string? jsonArray = ExtractJsonArray(llmOutput);
        if (jsonArray == null)
        {
            Debug.WriteLine("[BreakdownParser] No JSON array found in LLM output.");
            return new List<BreakdownPart>();
        }

        List<RawPart>? raw;
        try
        {
            raw = JsonSerializer.Deserialize<List<RawPart>>(jsonArray);
        }
        catch (JsonException ex)
        {
            Debug.WriteLine($"[BreakdownParser] JSON deserialize failed: {ex.Message}");
            return new List<BreakdownPart>();
        }

        if (raw == null || raw.Count == 0)
            return new List<BreakdownPart>();

        var parts = new List<BreakdownPart>();
        foreach (var r in raw)
        {
            if (string.IsNullOrEmpty(r.Surface))
                continue;

            var kind = r.Kind is "g" or "grammar"
                ? BreakdownPartKind.Grammar
                : BreakdownPartKind.Vocab;
            string? reading = string.IsNullOrWhiteSpace(r.Reading) ? null : r.Reading.Trim();

            parts.Add(new BreakdownPart(r.Surface, reading, r.Meaning?.Trim() ?? "", kind));
        }

        if (parts.Count == 0)
            return new List<BreakdownPart>();

        // Alignment validation: concatenated surfaces must reproduce the original text.
        string concatenated = string.Concat(parts.Select(p => p.Surface));
        string originalNorm = NormalizeWhitespace(originalText);
        string surfacesNorm = NormalizeWhitespace(concatenated);

        if (!string.Equals(originalNorm, surfacesNorm, StringComparison.Ordinal))
        {
            Debug.WriteLine(
                $"[BreakdownParser] Alignment validation failed: expected '{originalNorm}', got '{surfacesNorm}'.");
            return new List<BreakdownPart>();
        }

        return parts;
    }

    /// <summary>
    /// Extracts the JSON array from raw LLM output, tolerating markdown code
    /// fences and surrounding commentary by slicing from the first '[' to the
    /// last ']'.
    /// </summary>
    private static string? ExtractJsonArray(string text)
    {
        text = Regex.Replace(text, @"```(?:json)?", "");

        int start = text.IndexOf('[');
        int end = text.LastIndexOf(']');
        if (start < 0 || end <= start)
            return null;

        return text[start..(end + 1)];
    }

    /// <summary>
    /// Removes all whitespace for alignment comparison.
    /// </summary>
    private static string NormalizeWhitespace(string text)
        => Regex.Replace(text, @"\s+", "");
}
