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
/// Provider adapter for any service implementing the OpenAI REST protocol (Groq, Together, DeepSeek, Ollama, LM Studio, vLLM).
/// Supports custom base URLs, optional API keys for local LAN models, and selective capability configuration.
/// </summary>
public sealed class OpenAiCompatibleProviderAdapter : IAiProviderAdapter
{
    public string Kind => AiProviderKinds.OpenAiCompatible;

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
            return null; // Fall back to mock
        }

        var effectiveModel = string.IsNullOrWhiteSpace(model) ? GetDefaultModel(AiCapability.SpeechToText) : model;
        var customOptions = Microsoft.Extensions.Options.Options.Create(new AiServiceOptions
        {
            UseMock = false,
            SpeechToTextTimeoutSeconds = 15,
            OpenAI = new OpenAiOptions
            {
                ApiKey = profile.ApiKey,
                BaseUrl = profile.BaseUrl,
                WhisperModel = effectiveModel,
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
            return null; // Fall back to mock
        }

        var effectiveModel = string.IsNullOrWhiteSpace(model) ? GetDefaultModel(AiCapability.TextToSpeech) : model;
        var customOptions = Microsoft.Extensions.Options.Options.Create(new AiServiceOptions
        {
            UseMock = false,
            TextToSpeechTimeoutSeconds = 10,
            TtsCacheDirectory = cacheDirectory,
            OpenAI = new OpenAiOptions
            {
                ApiKey = profile.ApiKey,
                BaseUrl = profile.BaseUrl,
                TtsModel = effectiveModel,
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
            return null; // Fall back to mock
        }

        var effectiveModel = string.IsNullOrWhiteSpace(model) ? GetDefaultModel(AiCapability.FollowUpLlm) : model;
        var customOptions = Microsoft.Extensions.Options.Options.Create(new AiServiceOptions
        {
            UseMock = false,
            FollowUpTimeoutSeconds = 15,
            OpenAI = new OpenAiOptions
            {
                ApiKey = profile.ApiKey,
                BaseUrl = profile.BaseUrl,
                Model = effectiveModel,
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
            return null; // Fall back to mock
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
        if (string.IsNullOrWhiteSpace(profile.BaseUrl))
        {
            return (false, "Chưa nhập Base URL cho hồ sơ OpenAI-compatible.");
        }

        var isLocal = NetworkEndpointHelper.IsLocalOrLan(profile.BaseUrl);

        if (string.IsNullOrWhiteSpace(profile.ApiKey) && !isLocal)
        {
            return (false, "Chưa cấu hình API key cho hồ sơ OpenAI-compatible.");
        }

        var baseUrl = profile.BaseUrl.TrimEnd('/');
        var endpoint = $"{baseUrl}/models";
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        if (!string.IsNullOrWhiteSpace(profile.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", profile.ApiKey);
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linkedCts.CancelAfter(TimeSpan.FromSeconds(10));

        try
        {
            var response = await httpClient.SendAsync(request, linkedCts.Token).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return (true, "Kết nối OpenAI-compatible thành công (HTTP 200 OK).");
            }

            var status = (int)response.StatusCode;
            if (status is 401 or 403)
            {
                return (false, "API key không hợp lệ hoặc không có quyền truy cập (HTTP 401/403).");
            }
            if (status == 429)
            {
                return (false, "Đã vượt hạn mức yêu cầu (Rate limit HTTP 429).");
            }

            var body = await response.Content.ReadAsStringAsync(linkedCts.Token).ConfigureAwait(false);
            var sanitizedBody = AiKeySanitizer.Sanitize(body, profile.ApiKey);
            var msg = ExtractErrorMessage(sanitizedBody) ?? $"HTTP {status} {response.ReasonPhrase}";
            return (false, $"Lỗi kết nối OpenAI-compatible (HTTP {status}): {msg}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (false, "Hết thời gian chờ khi kết nối tới OpenAI-compatible endpoint (timeout 10 giây).");
        }
        catch (HttpRequestException ex)
        {
            var sanitized = AiKeySanitizer.Sanitize(ex.Message, profile.ApiKey);
            return (false, $"Lỗi mạng khi kết nối tới endpoint: {sanitized}");
        }
        catch (Exception ex)
        {
            var sanitized = AiKeySanitizer.Sanitize(ex.Message, profile.ApiKey);
            return (false, $"Lỗi kiểm tra kết nối: {sanitized}");
        }
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(HttpClient httpClient, AiProviderProfile profile, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(profile.BaseUrl))
        {
            return Array.Empty<string>();
        }

        var baseUrl = profile.BaseUrl.TrimEnd('/');
        var endpoint = $"{baseUrl}/models";
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        if (!string.IsNullOrWhiteSpace(profile.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", profile.ApiKey);
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linkedCts.CancelAfter(TimeSpan.FromSeconds(8));

        try
        {
            var response = await httpClient.SendAsync(request, linkedCts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return Array.Empty<string>();
            }

            var body = await response.Content.ReadAsStringAsync(linkedCts.Token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var list = new List<string>();

            // Check standard OpenAI data[].id
            if (root.TryGetProperty("data", out var dataElem) && dataElem.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in dataElem.EnumerateArray())
                {
                    if (item.TryGetProperty("id", out var idElem))
                    {
                        var id = idElem.GetString();
                        if (!string.IsNullOrWhiteSpace(id)) list.Add(id);
                    }
                }
            }
            // Check Ollama / alternative models[].name
            else if (root.TryGetProperty("models", out var modelsElem) && modelsElem.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in modelsElem.EnumerateArray())
                {
                    if (item.TryGetProperty("name", out var nameElem))
                    {
                        var name = nameElem.GetString();
                        if (!string.IsNullOrWhiteSpace(name)) list.Add(name);
                    }
                }
            }

            return list.OrderBy(m => m).ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
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
