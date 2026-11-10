using AIVES.BLL.Interfaces;
using AIVES.BLL.Services;
using AIVES.DAL.Entities;
using AIVES.DAL.Repositories.Interfaces;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using StudentNameMVC.Pages.Account;
using Xunit;

namespace AIVES.Tests.Frontend;

/// <summary>
/// Sinh viên tự đăng ký từ /Account/Register. Quy tắc đã chốt: chỉ tạo được role Staff (1),
/// mọi tài khoản mới quy về một giảng viên phụ trách, và giảng viên vẫn do Admin tạo.
/// </summary>
public class StudentRegistrationTests
{
    private const int LecturerId = 7;

    [Fact]
    public async Task RegisterStudent_always_creates_staff_role_with_default_lecturer()
    {
        var repo = new StubAccountRepo(new SystemAccount
        {
            AccountId = LecturerId,
            AccountName = "Giảng viên A",
            AccountEmail = "gv.a@aives.local",
            AccountRole = 2
        });
        var service = new SystemAccountService(repo);

        var id = await service.RegisterStudentAsync(
            "Sinh Viên Mới",
            "sv.moi@aives.local",
            "Test@123",
            LecturerId);

        var created = Assert.Single(repo.Created);
        Assert.Equal(id, repo.LastCreatedId);
        Assert.Equal("Sinh Viên Mới", created.Name);
        Assert.Equal("sv.moi@aives.local", created.Email);
        // Không nhận role từ client: luôn là Staff.
        Assert.Equal(1, created.Role);
        // Luôn gắn giảng viên phụ trách.
        Assert.Equal(LecturerId, created.LecturerId);
    }

    [Fact]
    public async Task RegisterStudent_rejects_lecturer_id_that_is_not_a_lecturer()
    {
        // Tài khoản trỏ tới là sinh viên (role 1) chứ không phải giảng viên.
        var repo = new StubAccountRepo(new SystemAccount
        {
            AccountId = LecturerId,
            AccountName = "Sinh viên khác",
            AccountEmail = "sv.khac@aives.local",
            AccountRole = 1
        });
        var service = new SystemAccountService(repo);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RegisterStudentAsync("SV", "sv.b@aives.local", "Test@123", LecturerId));

        Assert.Empty(repo.Created);
    }

    [Fact]
    public async Task RegisterStudent_rejects_missing_lecturer()
    {
        var repo = new StubAccountRepo();
        var service = new SystemAccountService(repo);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RegisterStudentAsync("SV", "sv.c@aives.local", "Test@123", defaultLecturerId: 999));

        Assert.Empty(repo.Created);
    }

    [Fact]
    public async Task RegisterStudent_rejects_duplicate_email()
    {
        var repo = new StubAccountRepo(
            new SystemAccount { AccountId = LecturerId, AccountName = "GV", AccountEmail = "gv@aives.local", AccountRole = 2 },
            new SystemAccount { AccountId = 30, AccountName = "Đã tồn tại", AccountEmail = "sv.trung@aives.local", AccountRole = 1 });
        var service = new SystemAccountService(repo);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RegisterStudentAsync("SV", "sv.trung@aives.local", "Test@123", LecturerId));

        Assert.Empty(repo.Created);
    }

    [Fact]
    public async Task RegisterStudent_rejects_short_password()
    {
        var repo = new StubAccountRepo(
            new SystemAccount { AccountId = LecturerId, AccountName = "GV", AccountEmail = "gv@aives.local", AccountRole = 2 });
        var service = new SystemAccountService(repo);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.RegisterStudentAsync("SV", "sv.d@aives.local", "123", LecturerId));

        Assert.Empty(repo.Created);
    }

    [Fact]
    public async Task Register_page_shows_configured_default_lecturer()
    {
        var repo = new StubAccountRepo(
            new SystemAccount { AccountId = 3, AccountName = "GV Đầu", AccountEmail = "gv.dau@aives.local", AccountRole = 2 },
            new SystemAccount { AccountId = 9, AccountName = "GV Cấu Hình", AccountEmail = "gv.chon@aives.local", AccountRole = 2 });

        var model = CreateRegisterModel(new SystemAccountService(repo), "GV.CHON@aives.local");
        await model.OnGetAsync();

        // Cấu hình thắng thứ tự AccountId.
        Assert.Equal("GV Cấu Hình", model.LecturerName);
    }

    [Fact]
    public async Task Register_page_falls_back_to_first_lecturer_when_config_is_empty()
    {
        var repo = new StubAccountRepo(
            new SystemAccount { AccountId = 5, AccountName = "GV Năm", AccountEmail = "gv.nam@aives.local", AccountRole = 2 },
            new SystemAccount { AccountId = 9, AccountName = "GV Chín", AccountEmail = "gv.chin@aives.local", AccountRole = 2 });

        var model = CreateRegisterModel(new SystemAccountService(repo), configuredEmail: null);
        await model.OnGetAsync();

        Assert.Equal("GV Năm", model.LecturerName);
    }

    [Fact]
    public async Task Register_page_handles_system_without_lecturer()
    {
        var repo = new StubAccountRepo();
        var model = CreateRegisterModel(new SystemAccountService(repo), configuredEmail: null);

        await model.OnGetAsync();

        Assert.Null(model.LecturerName);
    }

    private static RegisterModel CreateRegisterModel(ISystemAccountService service, string? configuredEmail)
    {
        var settings = new Dictionary<string, string?>();
        if (configuredEmail is not null)
        {
            settings["Registration:DefaultLecturerEmail"] = configuredEmail;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        return new RegisterModel(service, configuration, NullLogger<RegisterModel>.Instance);
    }

    private sealed class StubAccountRepo(params SystemAccount[] accounts) : ISystemAccountRepository
    {
        public List<DAL.Repositories.Models.AccountCreate> Created { get; } = new();
        public int LastCreatedId { get; private set; } = 100;

        public Task<List<SystemAccount>> SearchAsync(string? keyword = null, byte? role = null,
            bool includeDeleted = true, CancellationToken ct = default)
        {
            var q = accounts.AsEnumerable();
            if (role is { } r) q = q.Where(a => a.AccountRole == r);
            if (!includeDeleted) q = q.Where(a => !a.IsDeleted);
            return Task.FromResult(q.ToList());
        }

        public Task<SystemAccount?> GetByIdAsync(int id, bool includeDeleted = false, CancellationToken ct = default) =>
            Task.FromResult(accounts.FirstOrDefault(a => a.AccountId == id && (includeDeleted || !a.IsDeleted)));

        public Task<SystemAccount?> GetByEmailAsync(string email, CancellationToken ct = default) =>
            Task.FromResult(accounts.FirstOrDefault(a =>
                string.Equals(a.AccountEmail, email, StringComparison.OrdinalIgnoreCase)));

        public Task<bool> EmailExistsAsync(string email, int? exceptId = null, CancellationToken ct = default) =>
            Task.FromResult(accounts.Any(a =>
                string.Equals(a.AccountEmail, email, StringComparison.OrdinalIgnoreCase) &&
                (!exceptId.HasValue || a.AccountId != exceptId.Value)));

        public Task<int> CreateAsync(DAL.Repositories.Models.AccountCreate input, CancellationToken ct = default)
        {
            Created.Add(input);
            return Task.FromResult(LastCreatedId);
        }

        public Task<bool> UpdateAsync(DAL.Repositories.Models.AccountUpdate input, CancellationToken ct = default) =>
            Task.FromResult(true);

        public Task<bool> UpdateProfileAsync(DAL.Repositories.Models.ProfileUpdate input, CancellationToken ct = default) =>
            Task.FromResult(true);

        public Task<bool> UpdatePasswordAsync(int id, string passwordHash, CancellationToken ct = default) =>
            Task.FromResult(true);

        public Task<bool> SoftDeleteAsync(int id, CancellationToken ct = default) => Task.FromResult(true);

        public Task<bool> IsReferencedAsync(int id, CancellationToken ct = default) => Task.FromResult(false);

        public Task<DAL.Repositories.Models.DeleteResult> HardDeleteAsync(int id, CancellationToken ct = default) =>
            Task.FromResult(DAL.Repositories.Models.DeleteResult.NotFound);

        public Task<bool> ResetPasswordAsync(int id, string passwordHash, string token, string newPassword,
            CancellationToken ct = default) => Task.FromResult(true);
    }
}
