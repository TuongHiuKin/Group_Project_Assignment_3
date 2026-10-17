using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AIVES.BLL.Interfaces.Ai;
using AIVES.BLL.Options;
using AIVES.BLL.Security;
using AIVES.BLL.Services.Ai;
using Microsoft.AspNetCore.Authentication;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StudentNameMVC.Pages.Admin;
using Xunit;

namespace AIVES.Tests.Frontend;

public sealed class AdminAiSettingsTests : IClassFixture<AdminAiSettingsTests.AdminCompositionFactory>
{
    public sealed class AdminCompositionFactory : WebApplicationFactory<global::Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["VivaInterview:Enabled"] = "true",
                ["AiServices:UseMock"] = "true",
                ["ConnectionStrings:DefaultConnection"] = "Server=unused;Database=AdminFixture;Integrated Security=true;TrustServerCertificate=true",
                ["DefaultAdmin:Email"] = "admin@example.test",
                ["DefaultAdmin:Password"] = "admin-password-test"
            }));
            builder.ConfigureTestServices(services =>
            {
                services.AddLogging(options => options.SetMinimumLevel(LogLevel.Error));
            });
        }
    }

    private readonly AdminCompositionFactory _factory;

    public AdminAiSettingsTests(AdminCompositionFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Get_AiSettings_AnonymousUser_IsRedirectedToLogin()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var response = await client.GetAsync("/Admin/AiSettings");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        Assert.Contains("/Account/Login", response.Headers.Location.ToString());
    }

    [Fact]
    public void OnGet_Never_Exposes_ApiKey_In_Model()
    {
        var fakeStore = new InMemoryCredentialStore("test-key-not-real-1234567890", useRealAi: true);
        var tester = new DummyConnectionTester(true, "OK");
        var model = CreatePageModel(fakeStore, tester);

        model.OnGet();

        Assert.Null(model.ApiKey);
        Assert.True(model.Status.HasApiKey);
        Assert.Equal("…7890", model.Status.KeyHint);
        Assert.True(model.UseRealAi);
    }

    [Fact]
    public async Task OnPostSave_WhenEnablingRealAiWithoutKey_AddsModelError()
    {
        var fakeStore = new InMemoryCredentialStore(null, useRealAi: false);
        var tester = new DummyConnectionTester(true, "OK");
        var model = CreatePageModel(fakeStore, tester);
        model.ApiKey = "";
        model.UseRealAi = true;

        var result = await model.OnPostSaveAsync();

        Assert.IsType<PageResult>(result);
        Assert.True(model.ModelState.ContainsKey(nameof(model.ApiKey)));
        Assert.False(fakeStore.SaveCalled);
    }

    [Fact]
    public async Task OnPostSave_WithValidKey_CallsStoreAndRedirects()
    {
        var fakeStore = new InMemoryCredentialStore(null, useRealAi: false);
        var tester = new DummyConnectionTester(true, "OK");
        var model = CreatePageModel(fakeStore, tester);
        const string fakeKey = "test-key-not-real-1234567890";
        model.ApiKey = fakeKey;
        model.UseRealAi = true;

        var result = await model.OnPostSaveAsync();

        var redirect = Assert.IsType<RedirectToPageResult>(result);
        Assert.Null(redirect.PageName); // Redirects to same page
        Assert.True(fakeStore.SaveCalled);
        Assert.Equal(fakeKey, fakeStore.SavedKey);
        Assert.True(fakeStore.SavedUseRealAi);
    }

    [Fact]
    public async Task OnPostClear_CallsClearAndRedirects()
    {
        var fakeStore = new InMemoryCredentialStore("test-key-not-real-1234567890", useRealAi: true);
        var tester = new DummyConnectionTester(true, "OK");
        var model = CreatePageModel(fakeStore, tester);

        var result = await model.OnPostClearAsync();

        var redirect = Assert.IsType<RedirectToPageResult>(result);
        Assert.Null(redirect.PageName);
        Assert.True(fakeStore.ClearCalled);
    }

    [Fact]
    public async Task ConnectionTester_Success_WhenApiReturns200()
    {
        var handler = new TestHttpHandler((req, ct) =>
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"data": []}""", Encoding.UTF8, "application/json")
            });
        });

        using var client = new HttpClient(handler);
        var store = new InMemoryCredentialStore("test-key-not-real-1234567890", true);
        var monitor = new TestOptionsMonitor<AiServiceOptions>(new AiServiceOptions());
        var tester = new OpenAiConnectionTester(client, store, monitor, NullLogger<OpenAiConnectionTester>.Instance);

        var (ok, message) = await tester.TestConnectionAsync();

        Assert.True(ok);
        Assert.Contains("200 OK", message);
    }

    [Fact]
    public async Task ConnectionTester_Failure_WhenApiReturns401_DoesNotContainKey()
    {
        const string fakeKey = "sk-test-SECRET-4f9a1234567890";
        var handler = new TestHttpHandler((req, ct) =>
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("""{"error": {"message": "Invalid API key."}}""", Encoding.UTF8, "application/json")
            });
        });

        using var client = new HttpClient(handler);
        var store = new InMemoryCredentialStore(fakeKey, true);
        var monitor = new TestOptionsMonitor<AiServiceOptions>(new AiServiceOptions());
        var tester = new OpenAiConnectionTester(client, store, monitor, NullLogger<OpenAiConnectionTester>.Instance);

        var (ok, message) = await tester.TestConnectionAsync();

        Assert.False(ok);
        Assert.Contains("401", message);
        Assert.DoesNotContain(fakeKey, message);
    }

    [Fact]
    public async Task Get_AiSettings_WhenVivaDisabled_Returns200ForAdmin()
    {
        using var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["VivaInterview:Enabled"] = "false"
            }));
            builder.ConfigureTestServices(services =>
            {
                services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = "TestAdmin";
                    options.DefaultChallengeScheme = "TestAdmin";
                }).AddScheme<AuthenticationSchemeOptions, TestAdminAuthHandler>("TestAdmin", _ => { });
            });
        });

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var response = await client.GetAsync("/Admin/AiSettings");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task OnPostTest_WhenConnectionTesterFails_SetsTestErrorMessageAndRedirects()
    {
        var fakeStore = new InMemoryCredentialStore(null, useRealAi: false);
        var tester = new DummyConnectionTester(false, "Chưa cấu hình OpenAI API key.");
        var model = CreatePageModel(fakeStore, tester);

        var result = await model.OnPostTestAsync();

        var redirect = Assert.IsType<RedirectToPageResult>(result);
        Assert.Null(redirect.PageName);
        Assert.Equal("Chưa cấu hình OpenAI API key.", model.TempData["TestErrorMessage"]);
    }

    private static AiSettingsModel CreatePageModel(IAiCredentialStore store, IAiConnectionTester tester)
    {
        var model = new AiSettingsModel(store, tester, NullLogger<AiSettingsModel>.Instance);
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, "ADMIN01"),
            new(ClaimTypes.Name, "Administrator"),
            new(ClaimTypes.Role, ApplicationRoles.Admin)
        };
        var identity = new ClaimsIdentity(claims, "TestAuth");
        var principal = new ClaimsPrincipal(identity);

        var httpContext = new DefaultHttpContext { User = principal };
        var pageContext = new PageContext { HttpContext = httpContext };
        model.PageContext = pageContext;
        model.TempData = new TempDataDictionary(httpContext, new TempDataProviderStub());
        return model;
    }

    private sealed class InMemoryCredentialStore : IAiCredentialStore
    {
        private string? _key;
        private bool _useRealAi;

        public bool SaveCalled { get; private set; }
        public string? SavedKey { get; private set; }
        public bool SavedUseRealAi { get; private set; }
        public bool ClearCalled { get; private set; }

        public InMemoryCredentialStore(string? initialKey, bool useRealAi)
        {
            _key = initialKey;
            _useRealAi = useRealAi;
        }

        public AiCredentialStatus GetStatus() =>
            new(!string.IsNullOrWhiteSpace(_key), _useRealAi, _key?.Length >= 4 ? "…" + _key[^4..] : null, DateTime.UtcNow);

        public string? GetApiKey() => _key;

        public Task SaveAsync(string? newApiKey, bool useRealAi, CancellationToken ct = default)
        {
            SaveCalled = true;
            if (!string.IsNullOrWhiteSpace(newApiKey)) _key = newApiKey;
            _useRealAi = useRealAi;
            SavedKey = _key;
            SavedUseRealAi = _useRealAi;
            return Task.CompletedTask;
        }

        public Task ClearAsync(CancellationToken ct = default)
        {
            ClearCalled = true;
            _key = null;
            _useRealAi = false;
            return Task.CompletedTask;
        }
    }

    private sealed class DummyConnectionTester : IAiConnectionTester
    {
        private readonly bool _ok;
        private readonly string _msg;

        public DummyConnectionTester(bool ok, string msg)
        {
            _ok = ok;
            _msg = msg;
        }

        public Task<(bool Ok, string Message)> TestConnectionAsync(CancellationToken ct = default) =>
            Task.FromResult((_ok, _msg));
    }

    private sealed class TempDataProviderStub : ITempDataProvider
    {
        private readonly Dictionary<string, object?> _values = new();
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }

    private sealed class TestOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public T CurrentValue { get; }
        public TestOptionsMonitor(T value) => CurrentValue = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class TestHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;
        public TestHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) => _handler = handler;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct) => _handler(req, ct);
    }

    private sealed class TestAdminAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public TestAdminAuthHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, "ADMIN01"),
                new Claim(ClaimTypes.Name, "Administrator"),
                new Claim(ClaimTypes.Role, ApplicationRoles.Admin)
            };
            var identity = new ClaimsIdentity(claims, "TestAdmin");
            var principal = new ClaimsPrincipal(identity);
            var ticket = new AuthenticationTicket(principal, "TestAdmin");
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }
}
