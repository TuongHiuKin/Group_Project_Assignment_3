using System.Security.Cryptography;
using System.Text;
using AIVES.BLL.Interfaces;
using AIVES.BLL.Models.VivaInterview;
using AIVES.DAL.Entities;
using AIVES.DAL.Repositories.Interfaces;
using Microsoft.Extensions.Logging;

namespace AIVES.BLL.Services.MockData;

public sealed record MockSeedResult(int AccountsCreated, int AttemptsCreated);

/// <summary>
/// Seeds <see cref="MockExamDataCatalog"/> into SQL Server. Idempotent: accounts are matched by email and
/// attempts use deterministic session ids, so running it on every startup never duplicates data.
/// </summary>
public sealed class MockExamDataSeeder
{
    private const byte StudentRole = 1;
    private const byte LecturerRole = 2;

    /// <summary>
    /// Demo-only lecturer account (G4 demo script). Seeded so the walkthrough in docs/G4-DEMO.md
    /// can actually sign in; there is no lecturer self-registration in the app.
    /// </summary>
    public const string DemoLecturerEmail = "gv01@aives.local";

    private readonly ISystemAccountService _accountService;
    private readonly ISystemAccountRepository _accountRepo;
    private readonly IVivaSessionRepository _sessionRepo;
    private readonly IVivaTurnRepository _turnRepo;
    private readonly ILogger<MockExamDataSeeder>? _logger;

    public MockExamDataSeeder(
        ISystemAccountService accountService,
        ISystemAccountRepository accountRepo,
        IVivaSessionRepository sessionRepo,
        IVivaTurnRepository turnRepo,
        ILogger<MockExamDataSeeder>? logger = null)
    {
        _accountService = accountService ?? throw new ArgumentNullException(nameof(accountService));
        _accountRepo = accountRepo ?? throw new ArgumentNullException(nameof(accountRepo));
        _sessionRepo = sessionRepo ?? throw new ArgumentNullException(nameof(sessionRepo));
        _turnRepo = turnRepo ?? throw new ArgumentNullException(nameof(turnRepo));
        _logger = logger;
    }

    public async Task<MockSeedResult> SeedAsync(CancellationToken ct = default)
    {
        var accountsCreated = 0;
        var attemptsCreated = 0;

        if (await EnsureLecturerAsync(ct)) accountsCreated++;

        foreach (var account in MockExamDataCatalog.TestAccounts)
        {
            if ((await EnsureAccountAsync(account, ct)).Created) accountsCreated++;
        }

        foreach (var student in MockExamDataCatalog.Students)
        {
            var (accountId, created) = await EnsureAccountAsync(student.Account, ct);
            if (created) accountsCreated++;

            foreach (var attempt in student.Attempts)
            {
                if (await EnsureAttemptAsync(student.Account, accountId.ToString(), attempt, ct)) attemptsCreated++;
            }
        }

        _logger?.LogInformation("Mock exam data seeded: {Accounts} account(s), {Attempts} attempt(s) created.", accountsCreated, attemptsCreated);
        return new MockSeedResult(accountsCreated, attemptsCreated);
    }

    /// <summary>Deterministic session id so the same mock attempt maps to the same row on every run.</summary>
    public static Guid SessionIdFor(MockAccount account, MockAttempt attempt)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes($"aives-mock:{account.Email}:{attempt.Key}"));
        return new Guid(bytes);
    }

    private async Task<bool> EnsureLecturerAsync(CancellationToken ct)
    {
        var existing = await _accountRepo.GetByEmailAsync(DemoLecturerEmail, ct);
        if (existing != null)
        {
            return false;
        }

        await _accountService.CreateAccountAsync(
            "Giảng viên Demo 01",
            DemoLecturerEmail,
            MockExamDataCatalog.DefaultPassword,
            LecturerRole,
            ct);
        return true;
    }

    private async Task<(int Id, bool Created)> EnsureAccountAsync(MockAccount account, CancellationToken ct)
    {
        var existing = await _accountRepo.GetByEmailAsync(account.Email, ct);
        if (existing != null)
        {
            return (existing.AccountId, false);
        }

        var id = await _accountService.CreateAccountAsync(account.Name, account.Email, MockExamDataCatalog.DefaultPassword, StudentRole, ct);
        return (id, true);
    }

    private async Task<bool> EnsureAttemptAsync(MockAccount account, string studentId, MockAttempt attempt, CancellationToken ct)
    {
        var sessionId = SessionIdFor(account, attempt);
        if (await _sessionRepo.GetByIdAsync(sessionId, ct) != null)
        {
            return false;
        }

        var at = attempt.StartedAtUtc;
        await _sessionRepo.CreateSessionAsync(new VivaSession
        {
            SessionId = sessionId,
            ExamId = MockExamDataCatalog.ExamId,
            StudentId = studentId,
            State = (byte)VivaSessionState.Created,
            StartedAtUtc = at
        }, ct);

        var ordinal = 0;
        foreach (var answer in attempt.Answers)
        {
            at = at.AddMinutes(2);
            await _turnRepo.CreateTurnAsync(new VivaSessionTurn
            {
                SessionId = sessionId,
                QuestionId = answer.QuestionId,
                TurnOrdinal = ++ordinal,
                TranscriptText = answer.InitialAnswer,
                ConfidenceScore = 0.93m,
                FollowUpQuestionText = answer.FollowUpQuestion,
                CreatedAtUtc = at,
                CompletedAtUtc = at
            }, ct);

            at = at.AddMinutes(1);
            await _turnRepo.CreateTurnAsync(new VivaSessionTurn
            {
                SessionId = sessionId,
                QuestionId = answer.QuestionId,
                TurnOrdinal = ++ordinal,
                TranscriptText = answer.FollowUpAnswer,
                ConfidenceScore = 0.9m,
                CreatedAtUtc = at,
                CompletedAtUtc = at
            }, ct);
        }

        // Completed last: a run interrupted mid-attempt leaves an in-progress session, never a half-graded completed one.
        await _sessionRepo.CompleteSessionAsync(sessionId, at.AddSeconds(30), ct);
        return true;
    }
}
