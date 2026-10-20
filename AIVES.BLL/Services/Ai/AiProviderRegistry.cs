using System.Collections.Concurrent;
using AIVES.BLL.Interfaces.Ai;

namespace AIVES.BLL.Services.Ai;

/// <summary>
/// Thread-safe in-memory registry for pluggable AI provider adapters.
/// </summary>
public sealed class AiProviderRegistry : IAiProviderRegistry
{
    private readonly ConcurrentDictionary<string, IAiProviderAdapter> _adapters = new(StringComparer.OrdinalIgnoreCase);

    public AiProviderRegistry(IEnumerable<IAiProviderAdapter>? initialAdapters = null)
    {
        if (initialAdapters != null)
        {
            foreach (var adapter in initialAdapters)
            {
                Register(adapter);
            }
        }
    }

    public void Register(IAiProviderAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        _adapters[adapter.Kind] = adapter;
    }

    public IAiProviderAdapter? GetAdapter(string kind)
    {
        if (string.IsNullOrWhiteSpace(kind)) return null;
        _adapters.TryGetValue(kind, out var adapter);
        return adapter;
    }

    public IReadOnlyCollection<IAiProviderAdapter> GetAllAdapters()
    {
        return _adapters.Values.ToList().AsReadOnly();
    }
}
