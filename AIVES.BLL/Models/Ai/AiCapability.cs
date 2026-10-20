namespace AIVES.BLL.Models.Ai;

/// <summary>
/// Bitwise flags representing the individual capabilities supported by an AI provider.
/// </summary>
[Flags]
public enum AiCapability
{
    None = 0,
    SpeechToText = 1,
    TextToSpeech = 2,
    FollowUpLlm = 4,
    All = SpeechToText | TextToSpeech | FollowUpLlm
}
