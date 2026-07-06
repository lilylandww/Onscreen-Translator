using System.Text.Json.Serialization;

namespace WpfAppTest.Providers.Furigana;

/// <summary>
/// Represents a single morphological segment with optional furigana reading.
/// </summary>
/// <param name="Surface">The surface form of the token (e.g. "国境").</param>
/// <param name="Reading">The hiragana reading, or null for pure-kana surfaces.</param>
/// <param name="Pos">Part-of-speech tag from the morphological analyzer.</param>
/// <param name="IsOov">True if the token was not found in the dictionary.</param>
public record FuriganaSegment(
    [property: JsonPropertyName("surface")] string Surface,
    [property: JsonPropertyName("reading")] string? Reading,
    [property: JsonPropertyName("pos")] string Pos,
    [property: JsonPropertyName("is_oov")] bool IsOov
);
