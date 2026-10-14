namespace WpfAppTest.Providers.Breakdown;

/// <summary>
/// The role a <see cref="BreakdownPart"/> plays in the sentence.
/// </summary>
public enum BreakdownPartKind
{
    /// <summary>A vocabulary word (content word / morpheme).</summary>
    Vocab,

    /// <summary>A grammar unit (particle, auxiliary, ending, or set pattern).</summary>
    Grammar
}

/// <summary>
/// A single non-overlapping span of the recognized sentence, produced by the
/// breakdown LLM. Concatenating <c>Surface</c> of all parts in order must
/// reproduce the original sentence (validated by <see cref="BreakdownParser"/>).
/// </summary>
/// <param name="Surface">The exact surface span (e.g. "国境", "の", "なければならない").</param>
/// <param name="Reading">Hiragana reading when the surface contains kanji; otherwise null.</param>
/// <param name="Meaning">Short English gloss (vocab) or function (grammar).</param>
/// <param name="Kind">Vocab or Grammar.</param>
public record BreakdownPart(
    string Surface,
    string? Reading,
    string Meaning,
    BreakdownPartKind Kind
);
