using System.Net.Http.Headers;
using System.Text.Json;
using AIVES.BLL.Interfaces.Ai;
using AIVES.BLL.Models.Ai;
using AIVES.BLL.Options;
using AIVES.BLL.Services.Ai.Mocks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AIVES.BLL.Services.Ai.Adapters;

/// <summary>
/// Provider adapter for official OpenAI REST API endpoints.
/// </summary>
public sealed class OpenAiProviderAdapter : IAiProviderAdapter
{
    public string Kind => AiProviderKinds.OpenAi;

    public AiCapability SupportedCapabilities => AiCapability.All;

    public string GetDefaultModel(AiCapability capability) => capability switch
    {
        AiCapability.SpeechToText => "whisper-1",
        AiCapability.TextToSpeech => "tts-1",
        AiCapability.FollowUpLlm => "gpt-4o-mini",
        AiCapability.Grading => "gpt-4o-mini",
        _ => "gpt-4o-mini"
    };

    public ISpeechToTextService? CreateSpeechToTextService(IServiceProvider sp, AiProviderProfile profile, string model)
    {
        if (!profile.CustomCapabilities.HasFlag(AiCapability.SpeechToText))
        {
            return null;
        }

        var customOptions = Microsoft.Extensions.Options.Options.Create(new AiServiceOptions
        {
            UseMock = false,
            SpeechToTextTimeoutSeconds = 15,
            OpenAI = new OpenAiOptions
            {
                ApiKey = profile.ApiKey,
                BaseUrl = string.IsNullOrWhiteSpace(profile.BaseUrl) ? "https://api.openai.com/v1" : profile.BaseUrl,
                WhisperModel = string.IsNullOrWhiteSpace(model) ? "whisper-1" : model,
                OrganizationId = profile.OrganizationId
            }
        });

        var httpClient = sp.GetRequiredService<HttpClient>();
        var logger = sp.GetService<ILogger<WhisperSpeechToTextService>>();
        var allowFallback = sp.GetService<IOptions<AiServiceOptions>>()?.Value.AllowMockFallback ?? false;
        var mock = allowFallback ? sp.GetService<MockSpeechToTextService>() : null;
        return new WhisperSpeechToTextService(httpClient, customOptions, logger, mock);
    }

    public ITextToSpeechService? CreateTextToSpeechService(
        IServiceProvider sp,
        AiProviderProfile profile,
        string model,
        string? voice,
        string? cacheDirectory)
    {
        if (!profile.CustomCapabilities.HasFlag(AiCapability.TextToSpeech))
        {
            return null;
        }

        var customOptions = Microsoft.Extensions.Options.Options.Create(new AiServiceOptions
        {
            UseMock = false,
            TextToSpeechTimeoutSeconds = 10,
            TtsCacheDirectory = cacheDirectory,
            OpenAI = new OpenAiOptions
            {
                ApiKey = profile.ApiKey,
                BaseUrl = string.IsNullOrWhiteSpace(profile.BaseUrl) ? "https://api.openai.com/v1" : profile.BaseUrl,
                TtsModel = string.IsNullOrWhiteSpace(model) ? "tts-1" : model,
                TtsVoice = string.IsNullOrWhiteSpace(voice) ? "alloy" : voice,
                OrganizationId = profile.OrganizationId
            }
        });

        var httpClient = sp.GetRequiredService<HttpClient>();
        var logger = sp.GetService<ILogger<OpenAiTextToSpeechService>>();
        var allowFallback = sp.GetService<IOptions<AiServiceOptions>>()?.Value.AllowMockFallback ?? false;
        var mock = allowFallback ? sp.GetService<MockTextToSpeechService>() : null;
        return new OpenAiTextToSpeechService(httpClient, customOptions, logger, mock, cacheDirectory);
    }

    public IFollowUpGeneratorService? CreateFollowUpGeneratorService(IServiceProvider sp, AiProviderProfile profile, string model)
    {
        if (!profile.CustomCapabilities.HasFlag(AiCapability.FollowUpLlm))
        {
            return null;
        }

        var customOptions = Microsoft.Extensions.Options.Options.Create(new AiServiceOptions
        {
            UseMock = false,
            FollowUpTimeoutSeconds = 15,
            OpenAI = new OpenAiOptions
            {
                ApiKey = profile.ApiKey,
                BaseUrl = string.IsNullOrWhiteSpace(profile.BaseUrl) ? "https://api.openai.com/v1" : profile.BaseUrl,
                Model = string.IsNullOrWhiteSpace(model) ? "gpt-4o-mini" : model,
                OrganizationId = profile.OrganizationId
            }
        });

        var httpClient = sp.GetRequiredService<HttpClient>();
        var logger = sp.GetService<ILogger<OpenAiFollowUpGeneratorService>>();
        var allowFallback = sp.GetService<IOptions<AiServiceOptions>>()?.Value.AllowMockFallback ?? false;
        var mock = allowFallback ? sp.GetService<MockFollowUpGeneratorService>() : null;
        return new OpenAiFollowUpGeneratorService(httpClient, customOptions, logger, mock);
    }

    public IAnswerGradingService? CreateAnswerGradingService(IServiceProvider sp, AiProviderProfile profile, string model)
    {
        if (!profile.CustomCapabilities.HasFlag(AiCapability.Grading))
        {
            return null;
        }

        var effectiveModel = string.IsNullOrWhiteSpace(model) ? GetDefaultModel(AiCapability.Grading) : model;
        var httpClient = sp.GetRequiredService<HttpClient>();
        var logger = sp.GetService<ILogger<OpenAiAnswerGradingService>>();
        var aiOptions = sp.GetService<IOptions<AiServiceOptions>>()?.Value;
        var allowFallback = aiOptions?.AllowMockFallback ?? false;
        var mock = allowFallback ? sp.GetService<MockAnswerGradingService>() : null;
        var timeout = aiOptions?.GradingTimeoutSeconds > 0 ? aiOptions.GradingTimeoutSeconds : 30;
        var validator = sp.GetService<GradingOutputValidator>() ?? new GradingOutputValidator();
        return new OpenAiAnswerGradingService(httpClient, profile, effectiveModel, timeoutSeconds: timeout, logger, mock, validator);
    }

    public async Task<(bool Ok, string Message)> TestConnectionAsync(HttpClient httpClient, AiProviderProfile profile, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(profile.ApiKey))
        {
            return (false, "Chưa cấu hình API key cho hồ sơ OpenAI.");
        }

        var baseUrl = string.IsNullOrWhiteSpace(profile.BaseUrl)
            ? "https://api.openai.com/v1"
            : profile.BaseUrl.TrimEnd('/');

        var endpoint = $"{baseUrl}/models";
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", profile.ApiKey);
        if (!string.IsNullOrWhiteSpace(profile.OrganizationId))
        {
            request.Headers.Add("OpenAI-Organization", profile.OrganizationId);
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linkedCts.CancelAfter(TimeSpan.FromSeconds(10));

        try
        {
            var response = await httpClient.SendAsync(request, linkedCts.Token).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return (true, "Kết nối OpenAI thành công (HTTP 200 OK).");
            }

            var status = (int)response.StatusCode;
            if (status is 401 or 403)
            {
                return (false, "API key không hợp lệ hoặc không có quyền truy cập OpenAI (HTTP 401/403).");
            }
            if (status == 429)
            {
                return (false, "Đã vượt hạn mức yêu cầu OpenAI (Rate limit / Quota exceeded HTTP 429).");
            }

            var body = await response.Content.ReadAsStringAsync(linkedCts.Token).ConfigureAwait(false);
            var sanitizedBody = AiKeySanitizer.Sanitize(body, profile.ApiKey);
            var msg = ExtractErrorMessage(sanitizedBody) ?? $"HTTP {status} {response.ReasonPhrase}";
            return (false, $"Lỗi kết nối OpenAI (HTTP {status}): {msg}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (false, "Hết thời gian chờ khi kết nối tới OpenAI (timeout 10 giây).");
        }
        catch (HttpRequestException ex)
        {
            var sanitized = AiKeySanitizer.Sanitize(ex.Message, profile.ApiKey);
            return (false, $"Lỗi mạng khi kết nối tới OpenAI: {sanitized}");
        }
        catch (Exception ex)
        {
            var sanitized = AiKeySanitizer.Sanitize(ex.Message, profile.ApiKey);
            return (false, $"Lỗi kiểm tra kết nối OpenAI: {sanitized}");
        }
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(HttpClient httpClient, AiProviderProfile profile, CancellationToken ct = default)
    {
        var fallback = new List<string> { "gpt-4o-mini", "gpt-4o", "whisper-1", "tts-1", "tts-1-hd" };
        if (string.IsNullOrWhiteSpace(profile.ApiKey))
        {
            return fallback;
        }

        var baseUrl = string.IsNullOrWhiteSpace(profile.BaseUrl)
            ? "https://api.openai.com/v1"
            : profile.BaseUrl.TrimEnd('/');

        var endpoint = $"{baseUrl}/models";
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", profile.ApiKey);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linkedCts.CancelAfter(TimeSpan.FromSeconds(8));

        try
        {
            var response = await httpClient.SendAsync(request, linkedCts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return fallback;
            }

            var body = await response.Content.ReadAsStringAsync(linkedCts.Token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("data", out var dataElem) && dataElem.ValueKind == JsonValueKind.Array)
            {
                var list = new List<string>();
                foreach (var item in dataElem.EnumerateArray())
                {
                    if (item.TryGetProperty("id", out var idElem))
                    {
                        var id = idElem.GetString();
                        if (!string.IsNullOrWhiteSpace(id))
                        {
                            list.Add(id);
                        }
                    }
                }

                if (list.Count > 0)
                {
                    return list.OrderBy(m => m).ToList();
                }
            }
        }
        catch
        {
            // Fallback
        }

        return fallback;
    }

    private static string? ExtractErrorMessage(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("error", out var errorElem))
            {
                if (errorElem.TryGetProperty("message", out var msgElem))
                {
                    return msgElem.GetString();
                }
            }
        }
        catch { }
        return null;
    }
}
