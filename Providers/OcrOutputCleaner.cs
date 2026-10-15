using System;
using System.Collections.Generic;

namespace WpfAppTest.Providers;

/// <summary>
/// Post-processing harness for LLM-based OCR output. Cleans up model chatter
/// to ensure only extracted text remains. Shared by all vision-LLM OCR providers.
/// </summary>
internal static class OcrOutputCleaner
{
    public static string Clean(string text)
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
}