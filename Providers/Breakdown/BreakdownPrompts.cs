namespace WpfAppTest.Providers.Breakdown;

/// <summary>
/// Shared prompts for all sentence-breakdown LLM providers (OpenAI-compatible,
/// Gemini, Anthropic). Keep in sync with <see cref="BreakdownParser"/>'s
/// short-keyed output contract.
/// </summary>
internal static class BreakdownPrompts
{
    public const string SystemPrompt =
        "You split Japanese sentences into parts for language learners. " +
        "Reply with ONLY a JSON array. No markdown, no code fences, no commentary.";

    public static string BuildUserPrompt(string text)
    {
        return
            "Split this Japanese sentence into non-overlapping parts (vocabulary and grammar).\n" +
            "\n" +
            "Output rules:\n" +
            "- Output ONLY a JSON array. Each element: " +
            "{\"s\":\"surface\",\"r\":\"reading or null\",\"m\":\"short English meaning\",\"t\":\"v\" or \"g\"}\n" +
            "- \"t\":\"v\" for vocabulary words, \"g\" for grammar units " +
            "(particles, auxiliaries, endings, conjunctions, set patterns like 〜なければならない or 〜てから).\n" +
            "- Concatenating every \"s\" in order must reproduce the input sentence exactly — " +
            "do not add, change, or drop any characters, including spaces and line breaks.\n" +
            "- \"r\": hiragana reading, only when \"s\" contains kanji; otherwise null.\n" +
            "- \"m\": brief English gloss (max ~6 words). For grammar, state its function " +
            "(e.g. \"possessive particle\", \"must; have to\").\n" +
            "- Keep a grammar unit in one element when its pieces work as a single function; " +
            "split vocabulary at natural word boundaries.\n" +
            "\n" +
            "Sentence:\n" +
            text;
    }
}