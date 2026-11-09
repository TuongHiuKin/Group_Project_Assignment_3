using AIVES.DAL.Entities;
using Microsoft.EntityFrameworkCore;

namespace AIVES.DAL.Context;

/// <summary>DB-first mapping; never creates, migrates or seeds the database.</summary>
public class AIVESDbContext : DbContext
{
    private readonly DbContextOptions<AIVESDbContext>? _options;

    public AIVESDbContext(DbContextOptions<AIVESDbContext> options) : base(options)
    {
        _options = options;
    }

    public DbSet<SystemAccount> SystemAccounts => Set<SystemAccount>();
    public DbSet<VivaQuestion> VivaQuestions => Set<VivaQuestion>();
    public DbSet<VivaSession> VivaSessions => Set<VivaSession>();
    public DbSet<VivaSessionTurn> VivaSessionTurns => Set<VivaSessionTurn>();
    public DbSet<VivaGradingResult> VivaGradingResults => Set<VivaGradingResult>();
    public DbSet<VivaGradeChangeLog> VivaGradeChangeLogs => Set<VivaGradeChangeLog>();
    public DbSet<VivaRubricCriterion> VivaRubricCriteria => Set<VivaRubricCriterion>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.HasDefaultSchema("dbo");

        modelBuilder.Entity<SystemAccount>(entity =>
        {
            entity.ToTable("SystemAccount", table =>
            {
                table.HasCheckConstraint("CK_SystemAccount_AccountName_NotBlank", "LEN(LTRIM(RTRIM([AccountName]))) > 0");
                table.HasCheckConstraint("CK_SystemAccount_AccountEmail_NotBlank", "LEN(LTRIM(RTRIM([AccountEmail]))) > 0");
                table.HasCheckConstraint("CK_SystemAccount_PasswordHash_NotBlank", "LEN(LTRIM(RTRIM([AccountPasswordHash]))) > 0");
                table.HasCheckConstraint("CK_SystemAccount_AccountRole", "[AccountRole] IN (1, 2)");
            });
            entity.HasKey(x => x.AccountId).HasName("PK_SystemAccount");
            entity.Property(x => x.AccountId).UseIdentityColumn();
            entity.Property(x => x.AccountName).HasMaxLength(100).IsRequired();
            entity.Property(x => x.AccountEmail).HasMaxLength(254).IsRequired().UseCollation("Latin1_General_100_CI_AS");
            // SQL enforces a UNIQUE constraint. Map uniqueness without making email
            // an immutable EF alternate key, so Account/Profile can update email.
            entity.HasIndex(x => x.AccountEmail).IsUnique().HasDatabaseName("UQ_SystemAccount_AccountEmail");
            entity.Property(x => x.AccountPasswordHash).HasMaxLength(512).IsRequired();
            entity.Property(x => x.AccountRole).HasColumnType("tinyint");
            entity.Property(x => x.IsDeleted).HasDefaultValue(false).IsRequired();
        });

        modelBuilder.Entity<VivaQuestion>(entity =>
        {
            entity.ToTable("VivaQuestion");
            entity.HasKey(x => x.QuestionId).HasName("PK_VivaQuestion");
            entity.Property(x => x.QuestionId).HasMaxLength(50).IsUnicode(false).ValueGeneratedNever();
            entity.Property(x => x.ExamId).HasMaxLength(50).IsUnicode(false).IsRequired();
            entity.Property(x => x.Title).HasMaxLength(250).IsRequired();
            entity.Property(x => x.Content).HasColumnType("nvarchar(max)").IsRequired();
            entity.Property(x => x.TimeLimitSeconds).HasDefaultValue(90);
            entity.Property(x => x.AudioUrl).HasMaxLength(500);
            entity.Property(x => x.BloomLevel).HasMaxLength(50);
            entity.HasIndex(x => x.ExamId).HasDatabaseName("IX_VivaQuestion_ExamId");
        });

        modelBuilder.Entity<VivaSession>(entity =>
        {
            entity.ToTable("VivaSession");
            entity.HasKey(x => x.SessionId).HasName("PK_VivaSession");
            entity.Property(x => x.SessionId).ValueGeneratedNever();
            entity.Property(x => x.ExamId).HasMaxLength(50).IsUnicode(false).IsRequired();
            entity.Property(x => x.StudentId).HasMaxLength(50).IsRequired();
            entity.Property(x => x.State).HasColumnType("tinyint");
            entity.Property(x => x.StartedAtUtc).HasPrecision(3);
            entity.Property(x => x.CompletedAtUtc).HasPrecision(3);
            entity.HasIndex(x => new { x.StudentId, x.ExamId }).HasDatabaseName("IX_VivaSession_StudentId_ExamId");
        });

        modelBuilder.Entity<VivaSessionTurn>(entity =>
        {
            entity.ToTable("VivaSessionTurn");
            entity.HasKey(x => x.TurnId).HasName("PK_VivaSessionTurn");
            entity.Property(x => x.TurnId).ValueGeneratedNever();
            entity.Property(x => x.QuestionId).HasMaxLength(50).IsUnicode(false).IsRequired();
            entity.Property(x => x.AudioUrl).HasMaxLength(500);
            entity.Property(x => x.TranscriptText).HasColumnType("nvarchar(max)");
            entity.Property(x => x.ConfidenceScore).HasPrecision(5, 2);
            entity.Property(x => x.FollowUpQuestionText).HasColumnType("nvarchar(max)");
            entity.Property(x => x.FollowUpAudioUrl).HasMaxLength(500);
            entity.Property(x => x.CreatedAtUtc).HasPrecision(3);
            entity.Property(x => x.CompletedAtUtc).HasPrecision(3);
            entity.HasOne(x => x.Session).WithMany(x => x.Turns).HasForeignKey(x => x.SessionId)
                .OnDelete(DeleteBehavior.Cascade).HasConstraintName("FK_VivaSessionTurn_VivaSession");
            entity.HasOne(x => x.Question).WithMany(x => x.Turns).HasForeignKey(x => x.QuestionId)
                .OnDelete(DeleteBehavior.ClientNoAction).HasConstraintName("FK_VivaSessionTurn_VivaQuestion");
            entity.HasIndex(x => new { x.SessionId, x.TurnOrdinal }).IsUnique()
                .HasDatabaseName("UQ_VivaSessionTurn_SessionId_TurnOrdinal");
            entity.HasIndex(x => x.QuestionId).HasDatabaseName("IX_VivaSessionTurn_QuestionId");
        });

        modelBuilder.Entity<VivaGradingResult>(entity =>
        {
            entity.ToTable("VivaGradingResult");
            entity.HasKey(x => x.Id).HasName("PK_VivaGradingResult");
            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.Property(x => x.QuestionId).HasMaxLength(50).IsUnicode(false).IsRequired();
            entity.Property(x => x.AiScore).HasPrecision(5, 2);
            entity.Property(x => x.AiFeedback).HasColumnType("nvarchar(max)");
            entity.Property(x => x.Strengths).HasColumnType("nvarchar(max)");
            entity.Property(x => x.Weaknesses).HasColumnType("nvarchar(max)");
            entity.Property(x => x.CriteriaOutcomesJson).HasColumnType("nvarchar(max)");
            entity.Property(x => x.AiProvider).HasMaxLength(100);
            entity.Property(x => x.AiModel).HasMaxLength(100);
            entity.Property(x => x.GradingState).HasColumnType("tinyint");
            entity.Property(x => x.TeacherFinalScore).HasPrecision(5, 2);
            entity.Property(x => x.TeacherNotes).HasColumnType("nvarchar(max)");
            entity.Property(x => x.FinalizedBy).HasMaxLength(100);
            entity.Property(x => x.FinalizedAtUtc).HasPrecision(3);
            entity.Property(x => x.RowVersion).IsRowVersion();

            entity.HasOne(x => x.Session).WithMany(x => x.GradingResults).HasForeignKey(x => x.SessionId)
                .OnDelete(DeleteBehavior.Cascade).HasConstraintName("FK_VivaGradingResult_VivaSession");
            entity.HasOne(x => x.Question).WithMany(x => x.GradingResults).HasForeignKey(x => x.QuestionId)
                .OnDelete(DeleteBehavior.ClientNoAction).HasConstraintName("FK_VivaGradingResult_VivaQuestion");

            entity.HasIndex(x => new { x.SessionId, x.QuestionId }).IsUnique()
                .HasDatabaseName("UQ_VivaGradingResult_SessionId_QuestionId");
            entity.HasIndex(x => x.QuestionId).HasDatabaseName("IX_VivaGradingResult_QuestionId");
        });

        modelBuilder.Entity<VivaGradeChangeLog>(entity =>
        {
            entity.ToTable("VivaGradeChangeLog", table =>
            {
                table.HasCheckConstraint("CK_VivaGradeChangeLog_Reason_NotBlank", "LEN(LTRIM(RTRIM([Reason]))) > 0");
            });
            entity.HasKey(x => x.Id).HasName("PK_VivaGradeChangeLog");
            entity.Property(x => x.Id).UseIdentityColumn();
            entity.Property(x => x.OldScore).HasPrecision(5, 2);
            entity.Property(x => x.NewScore).HasPrecision(5, 2).IsRequired();
            entity.Property(x => x.ChangedBy).HasMaxLength(100).IsRequired();
            entity.Property(x => x.Reason).HasColumnType("nvarchar(max)").IsRequired();
            entity.Property(x => x.ChangedAtUtc).HasPrecision(3);

            entity.HasOne(x => x.GradingResult).WithMany(x => x.ChangeLogs).HasForeignKey(x => x.GradingResultId)
                .OnDelete(DeleteBehavior.Cascade).HasConstraintName("FK_VivaGradeChangeLog_VivaGradingResult");

            entity.HasIndex(x => x.GradingResultId).HasDatabaseName("IX_VivaGradeChangeLog_GradingResultId");
            entity.HasIndex(x => new { x.GradingResultId, x.ChangedAtUtc }).IsDescending(false, true)
                .HasDatabaseName("IX_VivaGradeChangeLog_GradingResultId_ChangedAtUtc");
        });

        modelBuilder.Entity<VivaRubricCriterion>(entity =>
        {
            entity.ToTable("VivaRubricCriteria");
            entity.HasKey(x => x.Id).HasName("PK_VivaRubricCriteria");
            entity.Property(x => x.Id).HasMaxLength(50).IsUnicode(false).ValueGeneratedNever();
            entity.Property(x => x.QuestionId).HasMaxLength(50).IsUnicode(false).IsRequired();
            entity.Property(x => x.CriterionCode).HasMaxLength(50).IsUnicode(false).IsRequired();
            entity.Property(x => x.CriterionName).HasMaxLength(200).IsRequired();
            entity.Property(x => x.Description).HasColumnType("nvarchar(max)").IsRequired();
            entity.Property(x => x.Weight).HasPrecision(5, 2).IsRequired();
            entity.Property(x => x.MaxPoints).HasPrecision(5, 2).IsRequired();
            entity.Property(x => x.KeywordsJson).HasColumnType("nvarchar(max)");

            entity.HasOne(x => x.Question).WithMany(x => x.RubricCriteria).HasForeignKey(x => x.QuestionId)
                .OnDelete(DeleteBehavior.Cascade).HasConstraintName("FK_VivaRubricCriteria_VivaQuestion");

            entity.HasIndex(x => x.QuestionId).HasDatabaseName("IX_VivaRubricCriteria_QuestionId");
        });
    }
}

