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
/// sentence. Alignment is exact — every non-line-break character (spaces
/// included) must be reproduced — except line breaks, which are stripped from
/// surfaces and the comparison: they are the whitespace class the LLM
/// commonly reflows and cannot render inside an inline ruby grid container.
/// On any validation or parse failure, returns an empty list.
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

            // Line breaks cannot render inside an inline ruby/grid container —
            // strip them from surfaces before alignment and rendering.
            var surface = StripLineBreaks(r.Surface);
            if (string.IsNullOrEmpty(surface))
                continue;

            var kind = r.Kind is "g" or "grammar"
                ? BreakdownPartKind.Grammar
                : BreakdownPartKind.Vocab;
            string? reading = string.IsNullOrWhiteSpace(r.Reading) ? null : r.Reading.Trim();

            parts.Add(new BreakdownPart(surface, reading, r.Meaning?.Trim() ?? "", kind));
        }

        if (parts.Count == 0)
            return new List<BreakdownPart>();

        // Alignment validation: exact reproduction, line breaks excepted.
        string concatenated = string.Concat(parts.Select(p => p.Surface));
        string original = StripLineBreaks(originalText);

        if (!string.Equals(original, concatenated, StringComparison.Ordinal))
        {
            Debug.WriteLine(
                $"[BreakdownParser] Alignment validation failed: expected '{original}', got '{concatenated}'.");
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
        text = Regex.Replace(text, @"(?:```|~~~)(?:json)?", "");

        int start = text.IndexOf('[');
        int end = text.LastIndexOf(']');
        if (start < 0 || end <= start)
            return null;

        return text[start..(end + 1)];
    }

    /// <summary>
    /// Removes line break characters (CRLF, unicode line/paragraph separators)
    /// while preserving all other characters, spaces included.
    /// </summary>
    private static string StripLineBreaks(string text)
        => text
            .Replace("\r", "")
            .Replace("\n", "")
            .Replace("\u0085", "")
            .Replace("\u2028", "")
            .Replace("\u2029", "");
}
