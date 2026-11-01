using AIVES.DAL.Entities;
using Microsoft.EntityFrameworkCore;

namespace AIVES.DAL.Context;

/// <summary>DB-first mapping; never creates, migrates or seeds the database.</summary>
public class AIVESDbContext : DbContext
{
    public AIVESDbContext(DbContextOptions<AIVESDbContext> options) : base(options) { }

    public DbSet<SystemAccount> SystemAccounts => Set<SystemAccount>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<NewsArticle> NewsArticles => Set<NewsArticle>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<NewsTag> NewsTags => Set<NewsTag>();
    public DbSet<VivaQuestion> VivaQuestions => Set<VivaQuestion>();
    public DbSet<VivaSession> VivaSessions => Set<VivaSession>();
    public DbSet<VivaSessionTurn> VivaSessionTurns => Set<VivaSessionTurn>();
    public DbSet<VivaGradingResult> VivaGradingResults => Set<VivaGradingResult>();

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

        modelBuilder.Entity<Category>(entity =>
        {
            entity.ToTable("Category", table => table.HasCheckConstraint(
                "CK_Category_CategoryName_NotBlank", "LEN(LTRIM(RTRIM([CategoryName]))) > 0"));
            entity.HasKey(x => x.CategoryId).HasName("PK_Category");
            entity.Property(x => x.CategoryId).UseIdentityColumn();
            entity.Property(x => x.CategoryName).HasMaxLength(100).IsRequired();
            entity.Property(x => x.CategoryDescription).HasMaxLength(500);
        });

        modelBuilder.Entity<Tag>(entity =>
        {
            entity.ToTable("Tag", table => table.HasCheckConstraint(
                "CK_Tag_TagName_NotBlank", "LEN(LTRIM(RTRIM([TagName]))) > 0"));
            entity.HasKey(x => x.TagId).HasName("PK_Tag");
            entity.Property(x => x.TagId).UseIdentityColumn();
            entity.Property(x => x.TagName).HasMaxLength(100).IsRequired();
        });

        modelBuilder.Entity<NewsArticle>(entity =>
        {
            entity.ToTable("NewsArticle", table =>
            {
                table.HasCheckConstraint("CK_NewsArticle_NewsTitle_NotBlank", "LEN(LTRIM(RTRIM([NewsTitle]))) > 0");
                table.HasCheckConstraint("CK_NewsArticle_NewsContent_NotBlank", "LEN(LTRIM(RTRIM([NewsContent]))) > 0");
                table.HasCheckConstraint("CK_NewsArticle_NewsStatus", "[NewsStatus] IN (0, 1)");
                table.HasCheckConstraint("CK_NewsArticle_ModificationAudit",
                    "([UpdatedById] IS NULL AND [ModifiedDate] IS NULL) OR ([UpdatedById] IS NOT NULL AND [ModifiedDate] IS NOT NULL AND [ModifiedDate] >= [CreatedDate])");
            });
            entity.HasKey(x => x.NewsArticleId).HasName("PK_NewsArticle");
            entity.Property(x => x.NewsArticleId).UseIdentityColumn();
            entity.Property(x => x.NewsTitle).HasMaxLength(200).IsRequired();
            entity.Property(x => x.NewsContent).HasColumnType("nvarchar(max)").IsRequired();
            entity.Property(x => x.NewsStatus).HasColumnType("tinyint");
            entity.Property(x => x.CreatedDate).HasPrecision(3).HasDefaultValueSql("SYSUTCDATETIME()");
            entity.Property(x => x.ModifiedDate).HasPrecision(3);
            // ClientNoAction also prevents EF from silently nulling a tracked optional editor FK.
            entity.HasOne(x => x.Category).WithMany(x => x.NewsArticles).HasForeignKey(x => x.CategoryId)
                .OnDelete(DeleteBehavior.ClientNoAction).HasConstraintName("FK_NewsArticle_Category");
            entity.HasOne(x => x.CreatedBy).WithMany(x => x.CreatedNewsArticles).HasForeignKey(x => x.CreatedById)
                .OnDelete(DeleteBehavior.ClientNoAction).HasConstraintName("FK_NewsArticle_CreatedBy");
            entity.HasOne(x => x.UpdatedBy).WithMany(x => x.UpdatedNewsArticles).HasForeignKey(x => x.UpdatedById)
                .OnDelete(DeleteBehavior.ClientNoAction).HasConstraintName("FK_NewsArticle_UpdatedBy");
            entity.HasIndex(x => x.CategoryId).HasDatabaseName("IX_NewsArticle_CategoryId");
            entity.HasIndex(x => new { x.CreatedById, x.CreatedDate }).IsDescending(false, true)
                .HasDatabaseName("IX_NewsArticle_CreatedById_CreatedDate");
            entity.HasIndex(x => x.UpdatedById).HasDatabaseName("IX_NewsArticle_UpdatedById");
            entity.HasIndex(x => x.CreatedDate).IsDescending().HasDatabaseName("IX_NewsArticle_CreatedDate");
            entity.HasIndex(x => new { x.NewsStatus, x.CreatedDate }).IsDescending(false, true)
                .HasDatabaseName("IX_NewsArticle_NewsStatus_CreatedDate");
        });

        modelBuilder.Entity<NewsTag>(entity =>
        {
            entity.ToTable("NewsTag");
            entity.HasKey(x => new { x.NewsArticleId, x.TagId }).HasName("PK_NewsTag");
            entity.HasOne(x => x.NewsArticle).WithMany(x => x.NewsTags).HasForeignKey(x => x.NewsArticleId)
                .OnDelete(DeleteBehavior.Cascade).HasConstraintName("FK_NewsTag_NewsArticle");
            entity.HasOne(x => x.Tag).WithMany(x => x.NewsTags).HasForeignKey(x => x.TagId)
                .OnDelete(DeleteBehavior.ClientNoAction).HasConstraintName("FK_NewsTag_Tag");
            entity.HasIndex(x => x.TagId).HasDatabaseName("IX_NewsTag_TagId");
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
    }
}

