using System.ComponentModel.DataAnnotations;
using AIVES.BLL.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using StudentNameMVC.Security;

namespace StudentNameMVC.Pages.Account;

[AllowAnonymous]
[EnableRateLimiting("password-reset")]
public class ForgotPasswordModel : PageModel
{
    private readonly IAuthService _authService;
    private readonly IPasswordResetEmailSender _resetEmailSender;
    private readonly ILogger<ForgotPasswordModel> _logger;

    public ForgotPasswordModel(
        IAuthService authService,
        IPasswordResetEmailSender resetEmailSender,
        ILogger<ForgotPasswordModel> logger)
    {
        _authService = authService ?? throw new ArgumentNullException(nameof(authService));
        _resetEmailSender = resetEmailSender ?? throw new ArgumentNullException(nameof(resetEmailSender));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    [BindProperty]
    [Required(ErrorMessage = "Vui lòng nhập email.")]
    [EmailAddress(ErrorMessage = "Email không hợp lệ.")]
    public string Email { get; set; } = string.Empty;

    public bool Submitted { get; set; }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct = default)
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        try
        {
            var token = await _authService.CreatePasswordResetTokenAsync(Email, ct);
            if (token is not null)
            {
                await _resetEmailSender.SendAsync(Email.Trim().ToLowerInvariant(), token, ct);
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _logger.LogError("Password reset request could not be processed ({ErrorType}).", error.GetType().Name);
        }

        ModelState.Clear();
        Submitted = true;
        return Page();
    }
}
