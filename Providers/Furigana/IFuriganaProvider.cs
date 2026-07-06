using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WpfAppTest.Providers.Furigana;

/// <summary>
/// Provides furigana readings for Japanese text via an HTTP sidecar service.
/// </summary>
public interface IFuriganaProvider
{
    /// <summary>Human-readable name of this provider.</summary>
    string Name { get; }

    /// <summary>
    /// Analyzes the given text and returns morphological segments with furigana readings.
    /// </summary>
    /// <param name="text">Japanese text to analyze.</param>
    /// <param name="allowFallback">If true, the sidecar may invoke OOV fallback (FLFL) for unknown kanji.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of furigana segments. Never null.</returns>
    Task<List<FuriganaSegment>> GetFuriganaAsync(string text, bool allowFallback, CancellationToken ct = default);

    /// <summary>
    /// Checks whether the furigana sidecar is reachable and healthy.
    /// </summary>
    Task<bool> IsAvailableAsync();
}
