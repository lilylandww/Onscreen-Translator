using System.Threading;
using System.Threading.Tasks;

namespace WpfAppTest.Providers;

public interface IOCRProvider
{
    string Name { get; }
    string DisplayName { get; }
    Task<string> RecognizeTextAsync(string imagePath, CancellationToken ct = default);
    Task<bool> IsAvailableAsync();
}
