using System.Text.RegularExpressions;

namespace AIVES.BLL.Services.Ai;

/// <summary>
/// Sanitizes sensitive AI API credentials from logs, exceptions, and user-facing error messages.
/// </summary>
public static partial class AiKeySanitizer
{
    private const string Redacted = "[REDACTED_KEY]";

    // Matches OpenAI keys (sk-...), Google AI Studio / Gemini keys (AIzaSy...), or long alphanumeric bearer tokens
    [GeneratedRegex(@"(sk-[A-Za-z0-9_\-]{15,}|AIza[A-Za-z0-9_\-]{30,})", RegexOptions.Compiled)]
    private static partial Regex GenericApiKeyRegex();

    /// <summary>
    /// Replaces occurrences of known API keys or key patterns with [REDACTED_KEY].
    /// </summary>
    public static string Sanitize(string? text, params string?[] knownKeys)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var result = text;
        foreach (var key in knownKeys)
        {
            if (!string.IsNullOrWhiteSpace(key) && key.Length >= 6)
            {
                result = result.Replace(key, Redacted, StringComparison.OrdinalIgnoreCase);
            }
        }

        result = GenericApiKeyRegex().Replace(result, Redacted);
        return result;
    }
}
