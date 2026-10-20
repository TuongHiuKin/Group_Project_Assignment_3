namespace AIVES.BLL.Interfaces.Ai;

/// <summary>
/// Registry containing all registered AI provider adapters.
/// </summary>
public interface IAiProviderRegistry
{
    /// <summary>
    /// Registers an adapter instance.
    /// </summary>
    void Register(IAiProviderAdapter adapter);

    /// <summary>
    /// Finds an adapter by kind (case-insensitive).
    /// </summary>
    IAiProviderAdapter? GetAdapter(string kind);

    /// <summary>
    /// Returns all registered adapters.
    /// </summary>
    IReadOnlyCollection<IAiProviderAdapter> GetAllAdapters();
}
