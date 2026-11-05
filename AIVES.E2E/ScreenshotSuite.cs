using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Microsoft.Playwright.NUnit;

namespace AIVES.E2E;

[TestFixture]
[Category("Screenshot")]
public class ScreenshotSuite : PageTest
{
    private sealed class ScreenshotRequest
    {
        public string? Id { get; set; }
        public List<string>? Pages { get; set; }
    }

    [Test]
    public async Task CaptureScreenshotsWhenRequestedAsync()
    {
        var localDir = ResolveLocalDir();
        var requestFile = Path.Combine(localDir, "screenshots", "request.json");

        if (!File.Exists(requestFile))
        {
            Assert.Pass($"No screenshot request file found at: {requestFile}");
            return;
        }

        var json = await File.ReadAllTextAsync(requestFile);
        var request = JsonSerializer.Deserialize<ScreenshotRequest>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        if (request?.Pages == null || request.Pages.Count == 0)
        {
            Assert.Pass("Screenshot request has no pages to capture.");
            return;
        }

        var reqId = string.IsNullOrWhiteSpace(request.Id)
            ? DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")
            : request.Id;

        var outputDir = Path.Combine(localDir, "screenshots", reqId);
        Directory.CreateDirectory(outputDir);

        var baseUrl = Environment.GetEnvironmentVariable("AIVES_BASE_URL") ?? "http://localhost:5000";

        foreach (var pageRoute in request.Pages)
        {
            var targetUrl = pageRoute.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? pageRoute
                : $"{baseUrl.TrimEnd('/')}/{pageRoute.TrimStart('/')}";

            var routeSlug = Slugify(pageRoute);

            // Desktop viewport: 1280x800
            await Page.SetViewportSizeAsync(1280, 800);
            await Page.GotoAsync(targetUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
            var desktopPath = Path.Combine(outputDir, $"{routeSlug}-desktop.png");
            await Page.ScreenshotAsync(new PageScreenshotOptions
            {
                Path = desktopPath,
                FullPage = true
            });

            // Mobile viewport: 390x844
            await Page.SetViewportSizeAsync(390, 844);
            await Page.GotoAsync(targetUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
            var mobilePath = Path.Combine(outputDir, $"{routeSlug}-mobile.png");
            await Page.ScreenshotAsync(new PageScreenshotOptions
            {
                Path = mobilePath,
                FullPage = true
            });
        }
    }

    private static string Slugify(string path)
    {
        var trimmed = path.Trim('/', ' ', '\\');
        if (string.IsNullOrEmpty(trimmed)) return "root";
        var slug = Regex.Replace(trimmed, @"[^a-zA-Z0-9_-]+", "-").ToLowerInvariant();
        return string.IsNullOrEmpty(slug) ? "page" : slug;
    }

    private static string ResolveLocalDir()
    {
        try
        {
            var psi = new ProcessStartInfo("git", "rev-parse --path-format=absolute --git-common-dir")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc != null)
            {
                var stdout = proc.StandardOutput.ReadToEnd().Trim();
                proc.WaitForExit();
                if (proc.ExitCode == 0 && !string.IsNullOrWhiteSpace(stdout))
                {
                    var parent = Directory.GetParent(stdout)?.FullName;
                    if (!string.IsNullOrEmpty(parent))
                    {
                        return Path.Combine(parent, ".local");
                    }
                }
            }
        }
        catch
        {
        }

        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "AIVESSystem.sln")))
            {
                return Path.Combine(dir, ".local");
            }
            var parent = Directory.GetParent(dir);
            if (parent == null) break;
            dir = parent.FullName;
        }

        return Path.Combine(Directory.GetCurrentDirectory(), ".local");
    }
}
