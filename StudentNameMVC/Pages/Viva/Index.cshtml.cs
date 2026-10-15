using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace StudentNameMVC.Pages.Viva;

[Authorize]
public class IndexModel : PageModel
{
    private readonly ILogger<IndexModel> _logger;

    public IndexModel(ILogger<IndexModel> logger)
    {
        _logger = logger;
    }

    [BindProperty]
    public string? SessionIdInput { get; set; }

    public string? ErrorMessage { get; set; }

    public void OnGet()
    {
    }

    public IActionResult OnPost()
    {
        if (string.IsNullOrWhiteSpace(SessionIdInput))
        {
            ErrorMessage = "Vui lòng nhập mã phiên thi (Session ID).";
            return Page();
        }

        if (!Guid.TryParse(SessionIdInput.Trim(), out var sessionGuid) || sessionGuid == Guid.Empty)
        {
            ErrorMessage = "Mã phiên thi không hợp lệ (phải là định dạng GUID hợp lệ).";
            return Page();
        }

        return RedirectToPage("/Viva/Exam", new { sessionId = sessionGuid });
    }
}
