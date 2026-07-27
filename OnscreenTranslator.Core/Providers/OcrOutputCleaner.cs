using System;
using System.Collections.Generic;
using System.Linq;

namespace OnscreenTranslator.Core.Providers;

/// <summary>
/// Shared OCR output post-processing utilities.
/// Extracted from duplicated code in OllamaOCRProvider and OpenAIOCRProvider.
/// </summary>
public static class OcrOutputCleaner
{
    /// <summary>
    /// Post-processing harness that cleans up LLM output to ensure it contains
    /// only the extracted text. Handles common LLM tendency to add commentary
    /// or markdown fences.
    /// </summary>
    public static string CleanOcrOutput(string text)
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
                // Check if this line might be a preamble followed by actual text
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

    /// <summary>
    /// Collapses repeated OCR output. Some vision models emit the recognized text
    /// more than once, usually as two identical paragraphs separated by a blank line.
    /// Only fires when the output is clearly doubled — genuinely distinct paragraphs
    /// are preserved.
    /// </summary>
    public static string DeduplicateOcrOutput(string text)
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
}
