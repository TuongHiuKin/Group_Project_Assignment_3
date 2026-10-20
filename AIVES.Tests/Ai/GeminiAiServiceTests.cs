using System.Net;
using System.Text;
using System.Text.Json;
using AIVES.BLL.Interfaces.Ai;
using AIVES.BLL.Models.Ai;
using AIVES.BLL.Services.Ai;
using AIVES.BLL.Services.Ai.Adapters;
using AIVES.BLL.Services.Ai.Gemini;
using AIVES.BLL.Services.Ai.Mocks;
using Xunit;

namespace AIVES.Tests.Ai;

public sealed class GeminiAiServiceTests
{
    private sealed class DelegatingHandlerStub(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            if (request.Content != null)
            {
                LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }
            return await handler(request, cancellationToken);
        }
    }

    [Fact]
    public async Task Gemini_Stt_Sends_Header_Key_And_Cleans_MimeType_And_Sets_Confidence_90()
    {
        const string fakeKey = "AIzaSyFakeKey1234567890abcdef";
        var profile = new AiProviderProfile
        {
            Id = "gemini-1",
            Name = "Gemini",
            Kind = AiProviderKinds.Gemini,
            BaseUrl = "https://generativelanguage.googleapis.com/v1beta",
            ApiKey = fakeKey
        };

        var geminiResponseJson = """
        {
          "candidates": [
            {
              "content": {
                "parts": [
                  {
                    "text": "Tôi xin trả lời về kiến trúc 3 lớp của ASP.NET Core."
                  }
                ]
              }
            }
          ]
        }
        """;

        DelegatingHandlerStub? handler = null;
        handler = new DelegatingHandlerStub((req, ct) =>
        {
            // Verify key is in header x-goog-api-key
            Assert.True(req.Headers.Contains("x-goog-api-key"));
            Assert.Equal(fakeKey, req.Headers.GetValues("x-goog-api-key").First());

            // Verify key is NEVER in the URL or query string
            Assert.DoesNotContain(fakeKey, req.RequestUri!.ToString());
            Assert.True(string.IsNullOrEmpty(req.RequestUri.Query));

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(geminiResponseJson, Encoding.UTF8, "application/json")
            });
        });

        using var client = new HttpClient(handler);
        var stt = new GeminiSpeechToTextService(client, profile, "gemini-2.5-flash");

        using var audioStream = new MemoryStream(new byte[] { 0x01, 0x02, 0x03, 0x04 });
        var result = await stt.TranscribeAsync(audioStream, "audio/webm;codecs=opus");

        Assert.True(result.IsSuccess);
        Assert.Equal("Tôi xin trả lời về kiến trúc 3 lớp của ASP.NET Core.", result.TranscriptText);
        Assert.Equal(0.90m, result.ConfidenceScore); // Gemini fixed confidence

        // Verify codecs=opus was stripped in the request JSON
        Assert.NotNull(handler.LastRequestBody);
        Assert.Contains("\"mime_type\":\"audio/webm\"", handler.LastRequestBody);
        Assert.DoesNotContain("codecs=opus", handler.LastRequestBody);
    }

    [Fact]
    public async Task Gemini_Tts_Wraps_Raw_PCM_Into_Valid_WAV_And_Caches_Locally()
    {
        const string fakeKey = "AIzaSyFakeKey1234567890abcdef";
        var profile = new AiProviderProfile
        {
            Id = "gemini-1",
            Name = "Gemini",
            Kind = AiProviderKinds.Gemini,
            BaseUrl = "https://generativelanguage.googleapis.com/v1beta",
            ApiKey = fakeKey
        };

        // 100 bytes of raw PCM
        var rawPcm = new byte[100];
        for (int i = 0; i < rawPcm.Length; i++) rawPcm[i] = (byte)(i % 256);
        var base64Pcm = Convert.ToBase64String(rawPcm);

        var geminiTtsResponse = $$"""
        {
          "candidates": [
            {
              "content": {
                "parts": [
                  {
                    "inlineData": {
                      "mimeType": "audio/L16;rate=24000",
                      "data": "{{base64Pcm}}"
                    }
                  }
                ]
              }
            }
          ]
        }
        """;

        var tempDir = Path.Combine(Path.GetTempPath(), "aives_gemini_tts_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            int networkCallCount = 0;
            var handler = new DelegatingHandlerStub((req, ct) =>
            {
                networkCallCount++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(geminiTtsResponse, Encoding.UTF8, "application/json")
                });
            });

            using var client = new HttpClient(handler);
            var tts = new GeminiTextToSpeechService(client, profile, "gemini-2.5-flash-preview-tts", "Puck", tempDir);

            // First call -> hits network and writes cache
            var result1 = await tts.SynthesizeAsync("Câu hỏi thứ nhất là gì?");
            Assert.True(result1.IsSuccess);
            Assert.NotNull(result1.AudioData);
            Assert.Equal(1, networkCallCount);
            Assert.EndsWith(".wav", result1.AudioUrl);

            // Verify standard WAV header (44 bytes header + 100 bytes PCM = 144 bytes)
            var wav = result1.AudioData!;
            Assert.Equal(44 + rawPcm.Length, wav.Length);
            Assert.Equal("RIFF", Encoding.ASCII.GetString(wav[0..4]));
            Assert.Equal("WAVE", Encoding.ASCII.GetString(wav[8..12]));
            Assert.Equal("fmt ", Encoding.ASCII.GetString(wav[12..16]));
            var sampleRate = BitConverter.ToInt32(wav, 24);
            Assert.Equal(24000, sampleRate);

            // Second call -> serves from local disk cache, networkCallCount stays 1!
            var result2 = await tts.SynthesizeAsync("Câu hỏi thứ nhất là gì?");
            Assert.True(result2.IsSuccess);
            Assert.Equal(1, networkCallCount);
            Assert.Equal(result1.AudioUrl, result2.AudioUrl);
            Assert.Equal(result1.AudioData, result2.AudioData);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public async Task Gemini_FollowUp_Generates_Valid_Probing_Question_Conforming_To_Policy()
    {
        const string fakeKey = "AIzaSyFakeKey1234567890abcdef";
        var profile = new AiProviderProfile
        {
            Id = "gemini-1",
            Name = "Gemini",
            Kind = AiProviderKinds.Gemini,
            BaseUrl = "https://generativelanguage.googleapis.com/v1beta",
            ApiKey = fakeKey
        };

        var geminiResponseJson = """
        {
          "candidates": [
            {
              "content": {
                "parts": [
                  {
                    "text": "{\"followUpQuestion\": \"Tại sao bạn lại chọn Entity Framework Core thay vì Dapper trong trường hợp này?\", \"cognitiveIntent\": \"Deep-Dive\"}"
                  }
                ]
              }
            }
          ]
        }
        """;

        var handler = new DelegatingHandlerStub((req, ct) =>
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(geminiResponseJson, Encoding.UTF8, "application/json")
            });
        });

        using var client = new HttpClient(handler);
        var followUp = new GeminiFollowUpGeneratorService(client, profile, "gemini-2.5-flash");

        var result = await followUp.GenerateFollowUpQuestionAsync("Em hiểu gì về ORM?", "Em dùng EF Core để truy vấn dữ liệu.", "Software Engineering");

        Assert.True(result.IsSuccess);
        Assert.Equal("Tại sao bạn lại chọn Entity Framework Core thay vì Dapper trong trường hợp này?", result.FollowUpQuestionText);
        Assert.Equal("Deep-Dive", result.CognitiveIntent);
    }

    [Fact]
    public async Task Gemini_Connection_Tester_Handles_Success_And_Auth_Failure_Without_Key_Leak()
    {
        const string secretKey = "AIzaSySecretGeminiKey9988776655";
        var profile = new AiProviderProfile
        {
            Id = "gemini-test",
            Name = "Gemini Test",
            Kind = AiProviderKinds.Gemini,
            BaseUrl = "https://generativelanguage.googleapis.com/v1beta",
            ApiKey = secretKey
        };

        var adapter = new GeminiProviderAdapter();

        // 1. Success test
        var okHandler = new DelegatingHandlerStub((req, ct) =>
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"models": [{"name": "models/gemini-2.5-flash"}]}""", Encoding.UTF8, "application/json")
            });
        });
        using var okClient = new HttpClient(okHandler);
        var (ok, msgOk) = await adapter.TestConnectionAsync(okClient, profile);
        Assert.True(ok);
        Assert.Contains("200 OK", msgOk);

        // 2. Failure test (401)
        var failHandler = new DelegatingHandlerStub((req, ct) =>
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("{\"error\": {\"message\": \"API key " + secretKey + " is invalid\"}}", Encoding.UTF8, "application/json")
            });
        });
        using var failClient = new HttpClient(failHandler);
        var (failOk, msgFail) = await adapter.TestConnectionAsync(failClient, profile);
        Assert.False(failOk);
        Assert.Contains("401", msgFail);
        Assert.DoesNotContain(secretKey, msgFail); // Key must never leak in error message!
    }

    [Fact]
    public async Task Gemini_ListModels_Strips_Models_Prefix()
    {
        var profile = new AiProviderProfile
        {
            Id = "gemini-test",
            Kind = AiProviderKinds.Gemini,
            ApiKey = "AIzaSyTest123"
        };

        var handler = new DelegatingHandlerStub((req, ct) =>
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                {
                  "models": [
                    { "name": "models/gemini-2.5-flash" },
                    { "name": "models/gemini-2.5-pro" },
                    { "name": "models/gemini-2.0-flash" }
                  ]
                }
                """, Encoding.UTF8, "application/json")
            });
        });

        using var client = new HttpClient(handler);
        var adapter = new GeminiProviderAdapter();
        var models = await adapter.ListModelsAsync(client, profile);

        Assert.Equal(3, models.Count);
        Assert.Contains("gemini-2.5-flash", models);
        Assert.DoesNotContain("models/gemini-2.5-flash", models);
    }
}
