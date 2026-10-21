using System.ComponentModel.DataAnnotations;
using AIVES.BLL.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;

namespace StudentNameMVC.Pages.Account;

[AllowAnonymous]
[EnableRateLimiting("password-reset")]
public class ResetPasswordModel : PageModel
{
    private readonly IAuthService _authService;

    public ResetPasswordModel(IAuthService authService)
    {
        _authService = authService ?? throw new ArgumentNullException(nameof(authService));
    }

    [BindProperty(SupportsGet = true)]
    [Required, EmailAddress]
    public string Email { get; set; } = string.Empty;

    [BindProperty(SupportsGet = true)]
    [Required]
    public string Token { get; set; } = string.Empty;

    [BindProperty]
    [Required(ErrorMessage = "Vui lòng nhập mật khẩu mới.")]
    [StringLength(128, MinimumLength = 8, ErrorMessage = "Mật khẩu phải có từ 8 đến 128 ký tự.")]
    [DataType(DataType.Password)]
    [Display(Name = "Mật khẩu mới")]
    public string NewPassword { get; set; } = string.Empty;

    [BindProperty]
    [Required(ErrorMessage = "Vui lòng xác nhận mật khẩu.")]
    [Compare(nameof(NewPassword), ErrorMessage = "Mật khẩu xác nhận không khớp.")]
    [DataType(DataType.Password)]
    [Display(Name = "Xác nhận mật khẩu")]
    public string ConfirmPassword { get; set; } = string.Empty;

    public void OnGet(string? email = null, string? token = null)
    {
        Response.Headers["Referrer-Policy"] = "no-referrer";
        Response.Headers["Cache-Control"] = "no-store";
        Email = email ?? string.Empty;
        Token = token ?? string.Empty;
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct = default)
    {
        Response.Headers["Referrer-Policy"] = "no-referrer";
        Response.Headers["Cache-Control"] = "no-store";
        if (!ModelState.IsValid)
        {
            return Page();
        }

        if (!await _authService.ResetPasswordAsync(Email, Token, NewPassword, ct))
        {
            ModelState.AddModelError(string.Empty, "Liên kết khôi phục không hợp lệ, đã hết hạn hoặc đã được sử dụng.");
            return Page();
        }

        TempData["SuccessMessage"] = "Đã đặt lại mật khẩu. Vui lòng đăng nhập bằng mật khẩu mới.";
        return RedirectToPage("/Account/Login");
    }
}
