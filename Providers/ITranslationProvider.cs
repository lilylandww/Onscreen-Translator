using System.Threading;
using System.Threading.Tasks;

namespace WpfAppTest.Providers;

public interface ITranslationProvider
{
    string Name { get; }
    string DisplayName { get; }
    Task<string> TranslateAsync(string text, string sourceLang = "ja", string targetLang = "en", CancellationToken ct = default);
    Task<bool> IsAvailableAsync();
}
