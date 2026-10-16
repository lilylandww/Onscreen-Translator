using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WpfAppTest.Providers.Breakdown;

/// <summary>
/// Splits a recognized sentence into vocabulary and grammar parts
/// for the hoverable Breakdown overlay view.
/// </summary>
public interface ISentenceBreakdownProvider
{
    /// <summary>
    /// Analyzes <paramref name="text"/> and returns its parts.
    /// Returns an empty list on failure (caller falls back to plain text).
    /// </summary>
    Task<List<BreakdownPart>> BreakdownAsync(string text, CancellationToken ct = default);
}
