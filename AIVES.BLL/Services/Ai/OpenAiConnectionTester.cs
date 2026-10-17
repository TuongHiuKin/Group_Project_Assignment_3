using System.Net.Http.Headers;
using System.Text.Json;
using AIVES.BLL.Interfaces.Ai;
using AIVES.BLL.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AIVES.BLL.Services.Ai;

public sealed class OpenAiConnectionTester : IAiConnectionTester
{
    private readonly HttpClient _httpClient;
    private readonly IAiCredentialStore _credentialStore;
    private readonly IOptionsMonitor<AiServiceOptions> _optionsMonitor;
    private readonly ILogger<OpenAiConnectionTester> _logger;

    public OpenAiConnectionTester(
        HttpClient httpClient,
        IAiCredentialStore credentialStore,
        IOptionsMonitor<AiServiceOptions> optionsMonitor,
        ILogger<OpenAiConnectionTester> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _credentialStore = credentialStore ?? throw new ArgumentNullException(nameof(credentialStore));
        _optionsMonitor = optionsMonitor ?? throw new ArgumentNullException(nameof(optionsMonitor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<(bool Ok, string Message)> TestConnectionAsync(CancellationToken ct = default)
    {
        var apiKey = _credentialStore.GetApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            apiKey = _optionsMonitor.CurrentValue.OpenAI.ApiKey;
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return (false, "Chưa cấu hình OpenAI API key.");
        }

        var baseUrl = _optionsMonitor.CurrentValue.OpenAI.BaseUrl?.TrimEnd('/') ?? "https://api.openai.com/v1";
        var endpoint = $"{baseUrl}/models";

        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linkedCts.CancelAfter(TimeSpan.FromSeconds(10));

        try
        {
            var response = await _httpClient.SendAsync(request, linkedCts.Token).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return (true, "Kết nối OpenAI thành công (HTTP 200 OK).");
            }

            var body = await response.Content.ReadAsStringAsync(linkedCts.Token).ConfigureAwait(false);
            var errorMessage = ExtractErrorMessage(body) ?? $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";

            _logger.LogWarning("OpenAI connection test failed: HTTP {StatusCode}", (int)response.StatusCode);
            return (false, $"Lỗi kết nối OpenAI (HTTP {(int)response.StatusCode}): {errorMessage}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("OpenAI connection test timed out after 10 seconds.");
            return (false, "Hết thời gian chờ khi kết nối tới OpenAI (timeout 10 giây).");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Network error during OpenAI connection test.");
            return (false, $"Lỗi mạng khi kết nối tới OpenAI: {ex.Message}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during OpenAI connection test.");
            return (false, $"Lỗi bất ngờ khi kiểm tra kết nối: {ex.Message}");
        }
    }

    private static string? ExtractErrorMessage(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("error", out var errorElement))
            {
                if (errorElement.TryGetProperty("message", out var msgElement))
                {
                    return msgElement.GetString();
                }
            }
        }
        catch
        {
            // Fallback for non-JSON response
        }

        return json.Length > 200 ? json[..200] + "..." : json;
    }
}
