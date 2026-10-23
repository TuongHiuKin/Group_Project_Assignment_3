using System.Text.Json;
using AIVES.BLL.Interfaces.Ai;
using AIVES.BLL.Models.Ai;
using AIVES.BLL.Options;
using AIVES.BLL.Services.Ai.Gemini;
using AIVES.BLL.Services.Ai.Mocks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AIVES.BLL.Services.Ai.Adapters;

/// <summary>
/// Provider adapter for Google Gemini REST APIs.
/// </summary>
public sealed class GeminiProviderAdapter : IAiProviderAdapter
{
    public string Kind => AiProviderKinds.Gemini;

    public AiCapability SupportedCapabilities => AiCapability.All;

    public string GetDefaultModel(AiCapability capability) => GeminiModels.DefaultFor(capability);

    public ISpeechToTextService? CreateSpeechToTextService(IServiceProvider sp, AiProviderProfile profile, string model)
    {
        if (!profile.CustomCapabilities.HasFlag(AiCapability.SpeechToText))
        {
            return null;
        }

        var effectiveModel = string.IsNullOrWhiteSpace(model) ? GetDefaultModel(AiCapability.SpeechToText) : model;
        var httpClient = sp.GetRequiredService<HttpClient>();
        var logger = sp.GetService<ILogger<GeminiSpeechToTextService>>();
        var aiOptions = sp.GetService<IOptions<AiServiceOptions>>()?.Value;
        var allowFallback = aiOptions?.AllowMockFallback ?? false;
        var mock = allowFallback ? sp.GetService<MockSpeechToTextService>() : null;
        var timeout = aiOptions?.SpeechToTextTimeoutSeconds > 0 ? aiOptions.SpeechToTextTimeoutSeconds : 45;
        return new GeminiSpeechToTextService(httpClient, profile, effectiveModel, timeoutSeconds: timeout, logger, mock);
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

        var effectiveModel = string.IsNullOrWhiteSpace(model) ? GetDefaultModel(AiCapability.TextToSpeech) : model;
        var effectiveVoice = string.IsNullOrWhiteSpace(voice) ? "Puck" : voice;
        var httpClient = sp.GetRequiredService<HttpClient>();
        var logger = sp.GetService<ILogger<GeminiTextToSpeechService>>();
        var aiOptions = sp.GetService<IOptions<AiServiceOptions>>()?.Value;
        var allowFallback = aiOptions?.AllowMockFallback ?? false;
        var mock = allowFallback ? sp.GetService<MockTextToSpeechService>() : null;
        var timeout = aiOptions?.TextToSpeechTimeoutSeconds > 0 ? aiOptions.TextToSpeechTimeoutSeconds : 20;
        return new GeminiTextToSpeechService(httpClient, profile, effectiveModel, effectiveVoice, cacheDirectory, timeoutSeconds: timeout, logger, mock);
    }

    public IFollowUpGeneratorService? CreateFollowUpGeneratorService(IServiceProvider sp, AiProviderProfile profile, string model)
    {
        if (!profile.CustomCapabilities.HasFlag(AiCapability.FollowUpLlm))
        {
            return null;
        }

        var effectiveModel = string.IsNullOrWhiteSpace(model) ? GetDefaultModel(AiCapability.FollowUpLlm) : model;
        var httpClient = sp.GetRequiredService<HttpClient>();
        var validator = sp.GetService<FollowUpPolicyValidator>() ?? new FollowUpPolicyValidator();
        var logger = sp.GetService<ILogger<GeminiFollowUpGeneratorService>>();
        var aiOptions = sp.GetService<IOptions<AiServiceOptions>>()?.Value;
        var allowFallback = aiOptions?.AllowMockFallback ?? false;
        var mock = allowFallback ? sp.GetService<MockFollowUpGeneratorService>() : null;
        var timeout = aiOptions?.FollowUpTimeoutSeconds > 0 ? aiOptions.FollowUpTimeoutSeconds : 35;
        return new GeminiFollowUpGeneratorService(httpClient, profile, effectiveModel, validator, timeoutSeconds: timeout, logger, mock);
    }

    public async Task<(bool Ok, string Message)> TestConnectionAsync(HttpClient httpClient, AiProviderProfile profile, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(profile.ApiKey))
        {
            return (false, "Chưa cấu hình API key cho hồ sơ Gemini.");
        }

        var baseUrl = string.IsNullOrWhiteSpace(profile.BaseUrl)
            ? "https://generativelanguage.googleapis.com/v1beta"
            : profile.BaseUrl.TrimEnd('/');

        var endpoint = $"{baseUrl}/models";
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Add("x-goog-api-key", profile.ApiKey);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linkedCts.CancelAfter(TimeSpan.FromSeconds(10));

        try
        {
            var response = await httpClient.SendAsync(request, linkedCts.Token).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return (true, "Kết nối Gemini thành công (HTTP 200 OK).");
            }

            var status = (int)response.StatusCode;
            if (status is 401 or 403)
            {
                return (false, "API key không hợp lệ hoặc không có quyền truy cập Gemini (HTTP 401/403).");
            }
            if (status == 429)
            {
                return (false, "Đã vượt hạn mức yêu cầu Gemini (Rate limit / Quota exceeded HTTP 429).");
            }

            var body = await response.Content.ReadAsStringAsync(linkedCts.Token).ConfigureAwait(false);
            var sanitizedBody = AiKeySanitizer.Sanitize(body, profile.ApiKey);
            var msg = ExtractErrorMessage(sanitizedBody) ?? $"HTTP {status} {response.ReasonPhrase}";
            return (false, $"Lỗi kết nối Gemini (HTTP {status}): {msg}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (false, "Hết thời gian chờ khi kết nối tới Gemini (timeout 10 giây).");
        }
        catch (HttpRequestException ex)
        {
            var sanitized = AiKeySanitizer.Sanitize(ex.Message, profile.ApiKey);
            return (false, $"Lỗi mạng khi kết nối tới Gemini: {sanitized}");
        }
        catch (Exception ex)
        {
            var sanitized = AiKeySanitizer.Sanitize(ex.Message, profile.ApiKey);
            return (false, $"Lỗi kiểm tra kết nối Gemini: {sanitized}");
        }
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(HttpClient httpClient, AiProviderProfile profile, CancellationToken ct = default)
    {
        return await TryListLiveModelsAsync(httpClient, profile, ct).ConfigureAwait(false) ?? GeminiModels.KnownModels;
    }

    /// <summary>
    /// Danh sách model thật từ Gemini <c>GET /models</c>; null khi không lấy được (thiếu key, lỗi mạng, HTTP lỗi),
    /// để nơi kiểm tra hợp lệ không nhầm danh sách dự phòng là danh sách thật.
    /// </summary>
    public async Task<IReadOnlyList<string>?> TryListLiveModelsAsync(HttpClient httpClient, AiProviderProfile profile, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(profile.ApiKey))
        {
            return null;
        }

        var baseUrl = string.IsNullOrWhiteSpace(profile.BaseUrl)
            ? "https://generativelanguage.googleapis.com/v1beta"
            : profile.BaseUrl.TrimEnd('/');

        var endpoint = $"{baseUrl}/models";
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Add("x-goog-api-key", profile.ApiKey);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linkedCts.CancelAfter(TimeSpan.FromSeconds(8));

        try
        {
            var response = await httpClient.SendAsync(request, linkedCts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(linkedCts.Token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("models", out var modelsElem) && modelsElem.ValueKind == JsonValueKind.Array)
            {
                var list = new List<string>();
                foreach (var item in modelsElem.EnumerateArray())
                {
                    if (item.TryGetProperty("name", out var nameElem))
                    {
                        var name = nameElem.GetString();
                        if (!string.IsNullOrWhiteSpace(name))
                        {
                            // Strip "models/" prefix if present
                            if (name.StartsWith("models/", StringComparison.OrdinalIgnoreCase))
                            {
                                name = name["models/".Length..];
                            }
                            list.Add(name);
                        }
                    }
                }

                if (list.Count > 0)
                {
                    return list;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Caller decides whether to fall back to known models.
        }

        return null;
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
