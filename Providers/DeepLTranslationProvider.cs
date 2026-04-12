using System.Threading;
using System.Threading.Tasks;
using DeepL;

namespace WpfAppTest.Providers;

public class DeepLTranslationProvider : ITranslationProvider
{
    private readonly Translator? _translator;

    public string Name => "deepl";
    public string DisplayName => "DeepL";

    public DeepLTranslationProvider(string? apiKey)
    {
        if (!string.IsNullOrWhiteSpace(apiKey))
            _translator = new Translator(apiKey);
    }

    public async Task<string> TranslateAsync(string text, string sourceLang = "ja", string targetLang = "en", CancellationToken ct = default)
    {
        if (_translator == null)
            throw new InvalidOperationException("DeepL translation skipped: API key is not configured.");

        var sourceLangCode = sourceLang == "ja" ? LanguageCode.Japanese : sourceLang;
        var targetLangCode = targetLang == "en" ? LanguageCode.EnglishAmerican : targetLang;

        var result = await _translator.TranslateTextAsync(text, sourceLangCode, targetLangCode);
        return result.ToString();
    }

    public Task<bool> IsAvailableAsync() => Task.FromResult(_translator != null);
}
