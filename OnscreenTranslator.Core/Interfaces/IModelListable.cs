using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OnscreenTranslator.Core.Providers;

/// <summary>
/// Interface for providers that can list available models.
/// </summary>
public interface IModelListable
{
    Task<List<string>> ListModelsAsync(CancellationToken ct = default);
}
