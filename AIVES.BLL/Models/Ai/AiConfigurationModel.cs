namespace AIVES.BLL.Models.Ai;

/// <summary>
/// Top-level model containing all provider profiles and per-capability routing assignments.
/// </summary>
public sealed class AiConfigurationModel
{
    public int SchemaVersion { get; set; } = 2;
    public bool UseRealAi { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
    public List<AiProviderProfile> Profiles { get; set; } = [];
    public AiCapabilityRouting Routing { get; set; } = new();

    public AiProviderProfile? FindProfile(string? profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId)) return null;
        return Profiles.FirstOrDefault(p => string.Equals(p.Id, profileId, StringComparison.OrdinalIgnoreCase));
    }
}
