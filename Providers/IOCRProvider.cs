using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WpfAppTest.Providers;

public interface IModelListable
{
    Task<List<string>> ListModelsAsync(CancellationToken ct = default);
}

public interface IOCRProvider
{
    string Name { get; }
    string DisplayName { get; }
    Task<string> RecognizeTextAsync(string imagePath, CancellationToken ct = default);
    Task<bool> IsAvailableAsync();
}
