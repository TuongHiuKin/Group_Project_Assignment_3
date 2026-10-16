using AIVES.BLL.Interfaces.Ai;
using AIVES.BLL.Interfaces.VivaInterview;
using AIVES.BLL.Models.VivaInterview;
using AIVES.BLL.Services.VivaInterview;
using AIVES.DAL.Context;
using AIVES.DAL.Entities;
using AIVES.DAL.Repositories.Implementations;
using AIVES.Tests.Realtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AIVES.Tests.Data;

[Trait("Category", "SqlServerWrite")]
public sealed class VivaRepositoryTests(TemporarySqlServerDatabase database) : IClassFixture<TemporarySqlServerDatabase>
{
    private sealed record ColumnSchema(
        string TableName,
        string ColumnName,
        string DataType,
        int? MaxLength,
        int? NumericPrecision,
        int? NumericScale,
        int? DatetimePrecision,
        string IsNullable);

    [SqlServerWriteFact]
    public async Task GetQuestionsByExamAsync_returns_seeded_questions_sorted_by_id()
    {
        await using var db = database.CreateContext();
        var repo = new VivaQuestionRepository(db);

        var questions = await repo.GetQuestionsByExamAsync("EXAM01");

        Assert.NotNull(questions);
        Assert.True(questions.Count >= 3);
        Assert.Equal(questions.OrderBy(x => x.QuestionId).Select(x => x.QuestionId), questions.Select(x => x.QuestionId));
        var first = await repo.GetByIdAsync("EXAM01-Q01");
        Assert.NotNull(first);
        Assert.Equal("EXAM01", first.ExamId);
        Assert.Equal("DbContext Lifetime", first.Title);
    }

    [SqlServerWriteFact]
    public async Task CreateSessionAsync_persists_and_is_idempotent_on_same_id()
    {
        await using var db = database.CreateContext();
        var repo = new VivaSessionRepository(db);
        var sessionId = Guid.NewGuid();
        var session = new VivaSession
        {
            SessionId = sessionId,
            ExamId = "EXAM01",
            StudentId = "student-001",
            State = 1,
            StartedAtUtc = DateTime.UtcNow
        };

        var createdId = await repo.CreateSessionAsync(session);
        Assert.Equal(sessionId, createdId);

        await using var verify = database.CreateContext();
        var readRepo = new VivaSessionRepository(verify);
        var readSession = await readRepo.GetByIdAsync(sessionId);
        Assert.NotNull(readSession);
        Assert.Equal("student-001", readSession.StudentId);
        Assert.Equal(1, readSession.State);

        // Calling a 2nd time with same ID does not duplicate or throw
        await using var secondDb = database.CreateContext();
        var secondRepo = new VivaSessionRepository(secondDb);
        var secondId = await secondRepo.CreateSessionAsync(session);
        Assert.Equal(sessionId, secondId);
    }

    [SqlServerWriteFact]
    public async Task UpdateStateAsync_persists_state_change_tracked_query_regression()
    {
        await using var db = database.CreateContext();
        var repo = new VivaSessionRepository(db);
        var sessionId = Guid.NewGuid();
        await repo.CreateSessionAsync(new VivaSession
        {
            SessionId = sessionId,
            ExamId = "EXAM01",
            StudentId = "student-002",
            State = 1,
            StartedAtUtc = DateTime.UtcNow
        });

        // Mutate state in another repository instance
        await using var updateDb = database.CreateContext();
        var updateRepo = new VivaSessionRepository(updateDb);
        await updateRepo.UpdateStateAsync(sessionId, 3);

        // Verify with a brand new context to ensure change was truly written to database
        await using var verifyDb = database.CreateContext();
        var verifyRepo = new VivaSessionRepository(verifyDb);
        var verified = await verifyRepo.GetByIdAsync(sessionId);
        Assert.NotNull(verified);
        Assert.Equal(3, verified.State);
    }

    [SqlServerWriteFact]
    public async Task CompleteSessionAsync_sets_state_to_completed_and_sets_timestamp()
    {
        await using var db = database.CreateContext();
        var repo = new VivaSessionRepository(db);
        var sessionId = Guid.NewGuid();
        await repo.CreateSessionAsync(new VivaSession
        {
            SessionId = sessionId,
            ExamId = "EXAM01",
            StudentId = "student-003",
            State = 2,
            StartedAtUtc = DateTime.UtcNow
        });

        var completedAt = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);
        await using var completeDb = database.CreateContext();
        var completeRepo = new VivaSessionRepository(completeDb);
        await completeRepo.CompleteSessionAsync(sessionId, completedAt);

        await using var verifyDb = database.CreateContext();
        var verifyRepo = new VivaSessionRepository(verifyDb);
        var session = await verifyRepo.GetByIdAsync(sessionId);
        Assert.NotNull(session);
        Assert.Equal(8, session.State); // CompletedState
        Assert.NotNull(session.CompletedAtUtc);
        Assert.Equal(completedAt, session.CompletedAtUtc.Value);
    }

    [SqlServerWriteFact]
    public async Task CreateTurn_SaveTranscript_SaveFollowUp_persists_all_fields()
    {
        await using var db = database.CreateContext();
        var sessionRepo = new VivaSessionRepository(db);
        var turnRepo = new VivaTurnRepository(db);

        var sessionId = Guid.NewGuid();
        await sessionRepo.CreateSessionAsync(new VivaSession
        {
            SessionId = sessionId,
            ExamId = "EXAM01",
            StudentId = "student-004",
            State = 2,
            StartedAtUtc = DateTime.UtcNow
        });

        var turnId = Guid.NewGuid();
        var createdTurnId = await turnRepo.CreateTurnAsync(new VivaSessionTurn
        {
            TurnId = turnId,
            SessionId = sessionId,
            QuestionId = "EXAM01-Q01",
            TurnOrdinal = 1,
            CreatedAtUtc = DateTime.UtcNow
        });
        Assert.Equal(turnId, createdTurnId);

        // Update transcript and confidence
        await using var transcriptDb = database.CreateContext();
        var transcriptRepo = new VivaTurnRepository(transcriptDb);
        await transcriptRepo.SaveTranscriptAsync(turnId, "Sample answer transcript", 0.95m);

        // Update follow-up
        await using var followUpDb = database.CreateContext();
        var followUpRepo = new VivaTurnRepository(followUpDb);
        await followUpRepo.SaveFollowUpAsync(turnId, "Sample follow-up question?", "/audio/followup.wav");

        // Verify in fresh context
        await using var verifyDb = database.CreateContext();
        var verifyRepo = new VivaTurnRepository(verifyDb);
        var turn = await verifyRepo.GetByIdAsync(turnId);
        Assert.NotNull(turn);
        Assert.Equal("Sample answer transcript", turn.TranscriptText);
        Assert.Equal(0.95m, turn.ConfidenceScore);
        Assert.NotNull(turn.CompletedAtUtc);
        Assert.Equal("Sample follow-up question?", turn.FollowUpQuestionText);
        Assert.Equal("/audio/followup.wav", turn.FollowUpAudioUrl);
    }

    [SqlServerWriteFact]
    public async Task GetWithDetailsAsync_includes_turns_collection()
    {
        await using var db = database.CreateContext();
        var sessionRepo = new VivaSessionRepository(db);
        var turnRepo = new VivaTurnRepository(db);

        var sessionId = Guid.NewGuid();
        await sessionRepo.CreateSessionAsync(new VivaSession
        {
            SessionId = sessionId,
            ExamId = "EXAM01",
            StudentId = "student-005",
            State = 2,
            StartedAtUtc = DateTime.UtcNow
        });

        await turnRepo.CreateTurnAsync(new VivaSessionTurn
        {
            TurnId = Guid.NewGuid(),
            SessionId = sessionId,
            QuestionId = "EXAM01-Q01",
            TurnOrdinal = 1,
            CreatedAtUtc = DateTime.UtcNow
        });

        await turnRepo.CreateTurnAsync(new VivaSessionTurn
        {
            TurnId = Guid.NewGuid(),
            SessionId = sessionId,
            QuestionId = "EXAM01-Q02",
            TurnOrdinal = 2,
            CreatedAtUtc = DateTime.UtcNow
        });

        // Fresh context for GetWithDetailsAsync
        await using var verifyDb = database.CreateContext();
        var verifyRepo = new VivaSessionRepository(verifyDb);
        var session = await verifyRepo.GetWithDetailsAsync(sessionId);

        Assert.NotNull(session);
        Assert.NotNull(session.Turns);
        Assert.Equal(2, session.Turns.Count);
    }

    [SqlServerWriteFact]
    public async Task GetTurnCountForSessionAsync_counts_turns_accurately()
    {
        await using var db = database.CreateContext();
        var sessionRepo = new VivaSessionRepository(db);
        var turnRepo = new VivaTurnRepository(db);

        var sessionId = Guid.NewGuid();
        await sessionRepo.CreateSessionAsync(new VivaSession
        {
            SessionId = sessionId,
            ExamId = "EXAM01",
            StudentId = "student-006",
            State = 2,
            StartedAtUtc = DateTime.UtcNow
        });

        Assert.Equal(0, await turnRepo.GetTurnCountForSessionAsync(sessionId));

        await turnRepo.CreateTurnAsync(new VivaSessionTurn
        {
            TurnId = Guid.NewGuid(),
            SessionId = sessionId,
            QuestionId = "EXAM01-Q01",
            TurnOrdinal = 1,
            CreatedAtUtc = DateTime.UtcNow
        });

        Assert.Equal(1, await turnRepo.GetTurnCountForSessionAsync(sessionId));
    }

    [SqlServerWriteFact]
    public async Task Missing_ids_do_not_throw_in_update_or_followup()
    {
        await using var db = database.CreateContext();
        var sessionRepo = new VivaSessionRepository(db);
        var turnRepo = new VivaTurnRepository(db);

        var missingSessionId = Guid.NewGuid();
        var missingTurnId = Guid.NewGuid();

        await sessionRepo.UpdateStateAsync(missingSessionId, 3);
        await sessionRepo.CompleteSessionAsync(missingSessionId, DateTime.UtcNow);
        await turnRepo.SaveTranscriptAsync(missingTurnId, "text", 0.5m);
        await turnRepo.SaveFollowUpAsync(missingTurnId, "question", "path");

        Assert.Null(await sessionRepo.GetByIdAsync(missingSessionId));
        Assert.Null(await turnRepo.GetByIdAsync(missingTurnId));
    }

    [SqlServerWriteFact]
    public async Task Viva_scripts_are_idempotent()
    {
        await using var db = database.CreateContext();
        var repo = new VivaQuestionRepository(db);

        // Run 003 and 004 a second time; must not throw
        await database.RunScriptAsync("003-create-viva-exam-core.sql");
        await database.RunScriptAsync("004-seed-viva-questions.sql");

        var questions = await repo.GetQuestionsByExamAsync("EXAM01");
        Assert.Equal(3, questions.Count);
    }

    [SqlServerWriteFact]
    public async Task Seeded_vietnamese_content_round_trips()
    {
        await using var db = database.CreateContext();
        var repo = new VivaQuestionRepository(db);

        var question = await repo.GetByIdAsync("EXAM01-Q01");
        Assert.NotNull(question);
        Assert.StartsWith("Hãy giải thích", question.Content);
        Assert.Equal("DbContext Lifetime", question.Title);
    }

    [SqlServerWriteFact]
    public async Task Duplicate_turn_ordinal_is_rejected_and_tracker_is_cleared()
    {
        await using var db = database.CreateContext();
        var sessionRepo = new VivaSessionRepository(db);
        var turnRepo = new VivaTurnRepository(db);

        var sessionId = Guid.NewGuid();
        await sessionRepo.CreateSessionAsync(new VivaSession
        {
            SessionId = sessionId,
            ExamId = "EXAM01",
            StudentId = "student-dup-ordinal",
            State = 2,
            StartedAtUtc = DateTime.UtcNow
        });

        var turn1 = new VivaSessionTurn
        {
            TurnId = Guid.NewGuid(),
            SessionId = sessionId,
            QuestionId = "EXAM01-Q01",
            TurnOrdinal = 1,
            CreatedAtUtc = DateTime.UtcNow
        };
        await turnRepo.CreateTurnAsync(turn1);

        var duplicateTurn = new VivaSessionTurn
        {
            TurnId = Guid.NewGuid(),
            SessionId = sessionId,
            QuestionId = "EXAM01-Q02",
            TurnOrdinal = 1, // Duplicate ordinal for same sessionId
            CreatedAtUtc = DateTime.UtcNow
        };

        // Calling on same context throws DbUpdateException due to UQ_VivaSessionTurn_SessionId_TurnOrdinal
        await Assert.ThrowsAsync<DbUpdateException>(() => turnRepo.CreateTurnAsync(duplicateTurn));

        // ChangeTracker must be cleared by SaveChangesSafeAsync
        Assert.Empty(db.ChangeTracker.Entries());

        // Context remains usable: can insert turn with TurnOrdinal = 2
        var turn2 = new VivaSessionTurn
        {
            TurnId = Guid.NewGuid(),
            SessionId = sessionId,
            QuestionId = "EXAM01-Q02",
            TurnOrdinal = 2,
            CreatedAtUtc = DateTime.UtcNow
        };
        var createdId = await turnRepo.CreateTurnAsync(turn2);
        Assert.Equal(turn2.TurnId, createdId);
    }

    [SqlServerWriteFact]
    public async Task Turn_with_unknown_question_violates_fk()
    {
        await using var db = database.CreateContext();
        var sessionRepo = new VivaSessionRepository(db);
        var turnRepo = new VivaTurnRepository(db);

        var sessionId = Guid.NewGuid();
        await sessionRepo.CreateSessionAsync(new VivaSession
        {
            SessionId = sessionId,
            ExamId = "EXAM01",
            StudentId = "student-fk-violation",
            State = 2,
            StartedAtUtc = DateTime.UtcNow
        });

        var turn = new VivaSessionTurn
        {
            TurnId = Guid.NewGuid(),
            SessionId = sessionId,
            QuestionId = "NOT-EXIST",
            TurnOrdinal = 1,
            CreatedAtUtc = DateTime.UtcNow
        };

        await Assert.ThrowsAsync<DbUpdateException>(() => turnRepo.CreateTurnAsync(turn));
    }

    [SqlServerWriteFact]
    public async Task Deleting_session_cascades_turns()
    {
        await using var db = database.CreateContext();
        var sessionRepo = new VivaSessionRepository(db);
        var turnRepo = new VivaTurnRepository(db);

        var sessionId = Guid.NewGuid();
        var session = new VivaSession
        {
            SessionId = sessionId,
            ExamId = "EXAM01",
            StudentId = "student-cascade",
            State = 2,
            StartedAtUtc = DateTime.UtcNow
        };
        await sessionRepo.CreateSessionAsync(session);

        await turnRepo.CreateTurnAsync(new VivaSessionTurn
        {
            TurnId = Guid.NewGuid(),
            SessionId = sessionId,
            QuestionId = "EXAM01-Q01",
            TurnOrdinal = 1,
            CreatedAtUtc = DateTime.UtcNow
        });

        await turnRepo.CreateTurnAsync(new VivaSessionTurn
        {
            TurnId = Guid.NewGuid(),
            SessionId = sessionId,
            QuestionId = "EXAM01-Q02",
            TurnOrdinal = 2,
            CreatedAtUtc = DateTime.UtcNow
        });

        // Delete session via DbContext
        db.VivaSessions.Remove(session);
        await db.SaveChangesAsync();

        // Verify with new context that turns were cascade-deleted in SQL Server
        await using var verifyDb = database.CreateContext();
        var remainingTurns = await verifyDb.VivaSessionTurns.Where(t => t.SessionId == sessionId).ToListAsync();
        Assert.Empty(remainingTurns);
    }

    [SqlServerWriteFact]
    public async Task Sql_schema_matches_ef_model_for_viva_tables()
    {
        await using var db = database.CreateContext();
        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT TABLE_NAME, COLUMN_NAME, DATA_TYPE, CHARACTER_MAXIMUM_LENGTH, 
                   NUMERIC_PRECISION, NUMERIC_SCALE, DATETIME_PRECISION, IS_NULLABLE
            FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_NAME IN ('VivaQuestion', 'VivaSession', 'VivaSessionTurn')
            ORDER BY TABLE_NAME, ORDINAL_POSITION";

        var columns = new List<ColumnSchema>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            columns.Add(new ColumnSchema(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetInt32(3),
                reader.IsDBNull(4) ? null : Convert.ToInt32(reader.GetValue(4)),
                reader.IsDBNull(5) ? null : Convert.ToInt32(reader.GetValue(5)),
                reader.IsDBNull(6) ? null : Convert.ToInt32(reader.GetValue(6)),
                reader.GetString(7)));
        }

        // Check VivaQuestion
        Assert.Contains(columns, c => c.TableName == "VivaQuestion" && c.ColumnName == "QuestionId" && c.DataType == "varchar" && c.MaxLength == 50 && c.IsNullable == "NO");
        Assert.Contains(columns, c => c.TableName == "VivaQuestion" && c.ColumnName == "ExamId" && c.DataType == "varchar" && c.MaxLength == 50 && c.IsNullable == "NO");
        Assert.Contains(columns, c => c.TableName == "VivaQuestion" && c.ColumnName == "Title" && c.DataType == "nvarchar" && c.MaxLength == 250 && c.IsNullable == "NO");
        Assert.Contains(columns, c => c.TableName == "VivaQuestion" && c.ColumnName == "Content" && c.DataType == "nvarchar" && c.MaxLength == -1 && c.IsNullable == "NO");
        Assert.Contains(columns, c => c.TableName == "VivaQuestion" && c.ColumnName == "TimeLimitSeconds" && c.DataType == "int" && c.IsNullable == "NO");
        Assert.Contains(columns, c => c.TableName == "VivaQuestion" && c.ColumnName == "AudioUrl" && c.DataType == "nvarchar" && c.MaxLength == 500 && c.IsNullable == "YES");

        // Check VivaSession
        Assert.Contains(columns, c => c.TableName == "VivaSession" && c.ColumnName == "SessionId" && c.DataType == "uniqueidentifier" && c.IsNullable == "NO");
        Assert.Contains(columns, c => c.TableName == "VivaSession" && c.ColumnName == "ExamId" && c.DataType == "varchar" && c.MaxLength == 50 && c.IsNullable == "NO");
        Assert.Contains(columns, c => c.TableName == "VivaSession" && c.ColumnName == "StudentId" && c.DataType == "nvarchar" && c.MaxLength == 50 && c.IsNullable == "NO");
        Assert.Contains(columns, c => c.TableName == "VivaSession" && c.ColumnName == "State" && c.DataType == "tinyint" && c.IsNullable == "NO");
        Assert.Contains(columns, c => c.TableName == "VivaSession" && c.ColumnName == "StartedAtUtc" && c.DataType == "datetime2" && c.DatetimePrecision == 3 && c.IsNullable == "NO");
        Assert.Contains(columns, c => c.TableName == "VivaSession" && c.ColumnName == "CompletedAtUtc" && c.DataType == "datetime2" && c.DatetimePrecision == 3 && c.IsNullable == "YES");

        // Check VivaSessionTurn
        Assert.Contains(columns, c => c.TableName == "VivaSessionTurn" && c.ColumnName == "TurnId" && c.DataType == "uniqueidentifier" && c.IsNullable == "NO");
        Assert.Contains(columns, c => c.TableName == "VivaSessionTurn" && c.ColumnName == "SessionId" && c.DataType == "uniqueidentifier" && c.IsNullable == "NO");
        Assert.Contains(columns, c => c.TableName == "VivaSessionTurn" && c.ColumnName == "QuestionId" && c.DataType == "varchar" && c.MaxLength == 50 && c.IsNullable == "NO");
        Assert.Contains(columns, c => c.TableName == "VivaSessionTurn" && c.ColumnName == "TurnOrdinal" && c.DataType == "int" && c.IsNullable == "NO");
        Assert.Contains(columns, c => c.TableName == "VivaSessionTurn" && c.ColumnName == "ConfidenceScore" && c.DataType == "decimal" && c.NumericPrecision == 5 && c.NumericScale == 2 && c.IsNullable == "YES");
        Assert.Contains(columns, c => c.TableName == "VivaSessionTurn" && c.ColumnName == "TranscriptText" && c.DataType == "nvarchar" && c.MaxLength == -1 && c.IsNullable == "YES");
        Assert.Contains(columns, c => c.TableName == "VivaSessionTurn" && c.ColumnName == "FollowUpQuestionText" && c.DataType == "nvarchar" && c.MaxLength == -1 && c.IsNullable == "YES");
        Assert.Contains(columns, c => c.TableName == "VivaSessionTurn" && c.ColumnName == "FollowUpAudioUrl" && c.DataType == "nvarchar" && c.MaxLength == 500 && c.IsNullable == "YES");
        Assert.Contains(columns, c => c.TableName == "VivaSessionTurn" && c.ColumnName == "CreatedAtUtc" && c.DataType == "datetime2" && c.DatetimePrecision == 3 && c.IsNullable == "NO");
        Assert.Contains(columns, c => c.TableName == "VivaSessionTurn" && c.ColumnName == "CompletedAtUtc" && c.DataType == "datetime2" && c.DatetimePrecision == 3 && c.IsNullable == "YES");
    }

    [SqlServerWriteFact]
    public async Task Orchestrator_full_flow_persists_with_real_repositories()
    {
        await using var db = database.CreateContext();
        var sessionRepo = new VivaSessionRepository(db);
        var questionRepo = new VivaQuestionRepository(db);
        var turnRepo = new VivaTurnRepository(db);
        var ai = new RealtimeAi();
        var publisher = new RecordingPublisher();

        var orchestrator = new VivaSessionOrchestrator(
            sessionRepo,
            questionRepo,
            turnRepo,
            ai,
            ai,
            ai,
            publisher,
            NullLogger<VivaSessionOrchestrator>.Instance,
            timers: null);

        // 1. InitializeSessionAsync
        var studentId = "student-e2e-" + Guid.NewGuid().ToString("N")[..8];
        var status = await orchestrator.InitializeSessionAsync("EXAM01", studentId);
        var sessionId = status.SessionId;
        Assert.NotEqual(Guid.Empty, sessionId);

        await using (var verifyDb = database.CreateContext())
        {
            var session = await new VivaSessionRepository(verifyDb).GetByIdAsync(sessionId);
            Assert.NotNull(session);
            Assert.Equal((byte)VivaSessionState.Created, session.State);
            Assert.Equal(studentId, session.StudentId);
        }

        // 2. StartSessionAsync
        var firstQuestion = await orchestrator.StartSessionAsync(sessionId);
        Assert.NotNull(firstQuestion);
        Assert.Equal("EXAM01-Q01", firstQuestion.QuestionId);

        await using (var verifyDb = database.CreateContext())
        {
            var session = await new VivaSessionRepository(verifyDb).GetByIdAsync(sessionId);
            Assert.NotNull(session);
            Assert.Equal((byte)VivaSessionState.QuestionDelivered, session.State);
        }

        // 3. Move state to Answering (as frontend/hub does when answering begins)
        await sessionRepo.UpdateStateAsync(sessionId, (byte)VivaSessionState.Answering);

        await using (var verifyDb = database.CreateContext())
        {
            var session = await new VivaSessionRepository(verifyDb).GetByIdAsync(sessionId);
            Assert.NotNull(session);
            Assert.Equal((byte)VivaSessionState.Answering, session.State);
        }

        // 4. SubmitAnswerAudioAsync
        var audioBytes = new byte[] { 0x1, 0x2, 0x3, 0x4 };
        var submission = new VivaAnswerSubmissionDto(
            SessionId: sessionId,
            QuestionId: firstQuestion.QuestionId,
            AudioData: audioBytes,
            ContentType: "audio/webm",
            DurationSeconds: 15
        );
        var transcriptDto = await orchestrator.SubmitAnswerAudioAsync(submission);
        Assert.NotNull(transcriptDto);
        Assert.False(string.IsNullOrWhiteSpace(transcriptDto.TranscriptText));

        Guid turnId;
        await using (var verifyDb = database.CreateContext())
        {
            var session = await new VivaSessionRepository(verifyDb).GetWithDetailsAsync(sessionId);
            Assert.NotNull(session);
            var turn = Assert.Single(session.Turns);
            turnId = turn.TurnId;
            Assert.Equal(1, turn.TurnOrdinal);
            Assert.Equal(transcriptDto.TranscriptText, turn.TranscriptText);
        }

        // 5. GenerateFollowUpAsync
        var followUpDto = await orchestrator.GenerateFollowUpAsync(sessionId, turnId);
        Assert.NotNull(followUpDto);
        Assert.False(string.IsNullOrWhiteSpace(followUpDto.FollowUpContent));

        await using (var verifyDb = database.CreateContext())
        {
            var session = await new VivaSessionRepository(verifyDb).GetByIdAsync(sessionId);
            Assert.NotNull(session);
            Assert.Equal((byte)VivaSessionState.FollowUpDelivered, session.State);

            var turn = await new VivaTurnRepository(verifyDb).GetByIdAsync(turnId);
            Assert.NotNull(turn);
            Assert.Equal(followUpDto.FollowUpContent, turn.FollowUpQuestionText);
        }

        // 6. CompleteSessionAsync
        var completedStatus = await orchestrator.CompleteSessionAsync(sessionId);
        Assert.Equal(VivaSessionState.Completed, completedStatus.State);

        await using (var verifyDb = database.CreateContext())
        {
            var session = await new VivaSessionRepository(verifyDb).GetByIdAsync(sessionId);
            Assert.NotNull(session);
            Assert.Equal((byte)VivaSessionState.Completed, session.State);
            Assert.NotNull(session.CompletedAtUtc);
        }
    }
}
