using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging;
using AIVES.BLL.Interfaces.Ai;
using StudentNameMVC.Security;

namespace StudentNameMVC.Pages.Admin;

[Authorize(Policy = AuthorizationPolicies.AdminOnly)]
public class AiSettingsModel : PageModel
{
    private readonly IAiCredentialStore _credentialStore;
    private readonly IAiConnectionTester _connectionTester;
    private readonly ILogger<AiSettingsModel> _logger;

    public AiSettingsModel(
        IAiCredentialStore credentialStore,
        IAiConnectionTester connectionTester,
        ILogger<AiSettingsModel> logger)
    {
        _credentialStore = credentialStore ?? throw new ArgumentNullException(nameof(credentialStore));
        _connectionTester = connectionTester ?? throw new ArgumentNullException(nameof(connectionTester));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public AiCredentialStatus Status { get; set; } = new(false, false, null, null);

    [BindProperty]
    public string? ApiKey { get; set; }

    [BindProperty]
    public bool UseRealAi { get; set; }

    public void OnGet()
    {
        Status = _credentialStore.GetStatus();
        UseRealAi = Status.UseRealAi;
        ApiKey = null; // Always empty when rendered
    }

    public async Task<IActionResult> OnPostSaveAsync(CancellationToken ct = default)
    {
        var currentStatus = _credentialStore.GetStatus();

        if (UseRealAi && !currentStatus.HasApiKey && string.IsNullOrWhiteSpace(ApiKey))
        {
            ModelState.AddModelError(nameof(ApiKey), "Vui lòng nhập OpenAI API key trước khi bật chế độ AI thật.");
        }

        if (!string.IsNullOrWhiteSpace(ApiKey))
        {
            var trimmed = ApiKey.Trim();
            if (trimmed.Length is < 20 or > 300 || trimmed.Any(char.IsWhiteSpace))
            {
                ModelState.AddModelError(nameof(ApiKey), "API key không hợp lệ (phải từ 20 đến 300 ký tự và không chứa khoảng trắng).");
            }
        }

        if (!ModelState.IsValid)
        {
            Status = currentStatus;
            return Page();
        }

        try
        {
            await _credentialStore.SaveAsync(ApiKey, UseRealAi, ct);
            var updatedStatus = _credentialStore.GetStatus();
            _logger.LogInformation("AI settings updated by {User}: UseRealAi={UseRealAi}, HasKey={HasKey}",
                User.Identity?.Name ?? "Admin", UseRealAi, updatedStatus.HasApiKey);

            TempData["SuccessMessage"] = "Đã lưu cấu hình AI thành công.";
            return RedirectToPage();
        }
        catch (ArgumentException ex)
        {
            ModelState.AddModelError(nameof(ApiKey), ex.Message);
            Status = currentStatus;
            return Page();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving AI settings.");
            ModelState.AddModelError(string.Empty, "Đã xảy ra lỗi khi lưu cấu hình AI.");
            Status = currentStatus;
            return Page();
        }
    }

    public async Task<IActionResult> OnPostClearAsync(CancellationToken ct = default)
    {
        await _credentialStore.ClearAsync(ct);
        _logger.LogInformation("AI settings updated by {User}: UseRealAi=False, HasKey=False",
            User.Identity?.Name ?? "Admin");

        TempData["SuccessMessage"] = "Đã xoá OpenAI API key. Hệ thống quay về chế độ mô phỏng (mock).";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostTestAsync(CancellationToken ct = default)
    {
        var (ok, message) = await _connectionTester.TestConnectionAsync(ct);
        if (ok)
        {
            TempData["TestSuccessMessage"] = message;
        }
        else
        {
            TempData["TestErrorMessage"] = message;
        }

        return RedirectToPage();
    }
}
