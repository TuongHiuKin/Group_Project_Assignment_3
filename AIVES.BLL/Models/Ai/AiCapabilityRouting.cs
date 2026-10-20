namespace AIVES.BLL.Models.Ai;

/// <summary>
/// Specifies the chosen provider profile and model for a single capability.
/// </summary>
public sealed class AiCapabilityTarget
{
    public string? ProfileId { get; set; }
    public string? Model { get; set; }
}

/// <summary>
/// Specifies the chosen provider profile, model, and voice for TTS capability.
/// </summary>
public sealed class AiTtsCapabilityTarget
{
    public string? ProfileId { get; set; }
    public string? Model { get; set; }
    public string? Voice { get; set; }
}

/// <summary>
/// Mapping of each core AI capability (STT, TTS, LLM) to a target provider profile and model.
/// </summary>
public sealed class AiCapabilityRouting
{
    public AiCapabilityTarget Stt { get; set; } = new();
    public AiTtsCapabilityTarget Tts { get; set; } = new();
    public AiCapabilityTarget Llm { get; set; } = new();
}
