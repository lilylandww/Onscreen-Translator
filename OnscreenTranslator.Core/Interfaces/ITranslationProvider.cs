using System.Threading;
using System.Threading.Tasks;

namespace OnscreenTranslator.Core.Providers;

public interface ITranslationProvider
{
    string Name { get; }
    string DisplayName { get; }
    Task<string> TranslateAsync(string text, string sourceLang = "ja", string targetLang = "en", CancellationToken ct = default);
    Task<bool> IsAvailableAsync();
}
