using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WpfAppTest.Providers.Furigana;

/// <summary>
/// Provides out-of-vocabulary (OOV) furigana fallback when the primary sidecar
/// cannot determine a reading for certain kanji tokens.
/// </summary>
public interface IFuriganaFallbackProvider
{
    /// <summary>
    /// Generates furigana readings for the given text, focusing on OOV kanji.
    /// </summary>
    /// <param name="text">Japanese text that contains unknown kanji.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of furigana segments from the fallback engine.</returns>
    Task<List<FuriganaSegment>> GetFallbackAsync(string text, CancellationToken ct = default);
}
