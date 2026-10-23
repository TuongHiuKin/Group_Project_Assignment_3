using System.Net;
using Microsoft.Extensions.Logging;

namespace AIVES.BLL.Services.Ai.Gemini;

/// <summary>
/// Thử lại request Gemini khi gặp lỗi quá tải tạm thời (HTTP 429, 503).
/// Mặc định thử lại tối đa 2 lần, chờ 1 giây rồi 3 giây; hết lần thử thì trả về response lỗi cuối cùng.
/// </summary>
public static class GeminiRetryPolicy
{
    public static readonly IReadOnlyList<TimeSpan> DefaultDelays = new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3) };

    public static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode == HttpStatusCode.TooManyRequests || statusCode == HttpStatusCode.ServiceUnavailable;

    public static async Task<HttpResponseMessage> SendAsync(
        HttpClient httpClient,
        HttpRequestMessage request,
        IReadOnlyList<TimeSpan> retryDelays,
        ILogger? logger,
        string operationName,
        CancellationToken ct)
    {
        // A sent HttpRequestMessage cannot be reused, so buffer the body to rebuild it for each retry.
        var body = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        var current = request;

        for (var attempt = 0; ; attempt++)
        {
            var response = await httpClient.SendAsync(current, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!IsTransient(response.StatusCode) || attempt >= retryDelays.Count)
            {
                return response;
            }

            logger?.LogWarning("{Operation} returned HTTP {StatusCode}; retrying in {DelaySeconds}s (retry {Retry}/{MaxRetries}).",
                operationName, (int)response.StatusCode, retryDelays[attempt].TotalSeconds, attempt + 1, retryDelays.Count);
            response.Dispose();

            await Task.Delay(retryDelays[attempt], ct).ConfigureAwait(false);
            current = Clone(request, body);
        }
    }

    private static HttpRequestMessage Clone(HttpRequestMessage original, byte[]? body)
    {
        var clone = new HttpRequestMessage(original.Method, original.RequestUri) { Version = original.Version };
        foreach (var header in original.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (body is not null && original.Content is not null)
        {
            clone.Content = new ByteArrayContent(body);
            foreach (var header in original.Content.Headers)
            {
                clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        return clone;
    }
}
