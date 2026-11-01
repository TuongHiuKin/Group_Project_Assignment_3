using AIVES.DAL.Context;
using AIVES.DAL;
using AIVES.DAL.Repositories.Implementations;
using AIVES.DAL.Repositories.Interfaces;
using AIVES.DAL.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;

namespace AIVES.Tests.Data;

public class AIVESDbContextTests
{
    // Model-only tests never open this connection.
    private static AIVESDbContext CreateContext() => new(
        new DbContextOptionsBuilder<AIVESDbContext>()
            .UseSqlServer("Server=unused;Database=ModelOnly;Integrated Security=True")
            .Options);

    [Fact]
    public void Model_has_news_and_viva_tables_and_no_shadow_columns()
    {
        using var context = CreateContext();
        var tables = context.Model.GetEntityTypes().ToArray();
        Assert.Equal(new[] { "Category", "NewsArticle", "NewsTag", "SystemAccount", "Tag", "VivaGradeChangeLog", "VivaGradingResult", "VivaQuestion", "VivaSession", "VivaSessionTurn" },
            tables.Select(x => x.GetTableName()).OrderBy(x => x));
        Assert.All(tables, table =>
        {
            Assert.Equal("dbo", table.GetSchema());
            Assert.DoesNotContain(table.GetProperties(), x => x.IsShadowProperty());
        });
        Assert.Equal(9, tables.Count(x => x.FindPrimaryKey()!.Properties.Count == 1));
        var newsIdentityTables = new[] { typeof(Category), typeof(NewsArticle), typeof(SystemAccount), typeof(Tag), typeof(VivaGradeChangeLog) };
        Assert.All(tables.Where(x => newsIdentityTables.Contains(x.ClrType)), table =>
            Assert.Equal(ValueGenerated.OnAdd, table.FindPrimaryKey()!.Properties.Single().ValueGenerated));
        var vivaNeverTables = new[] { typeof(VivaGradingResult), typeof(VivaQuestion), typeof(VivaSession), typeof(VivaSessionTurn) };
        Assert.All(tables.Where(x => vivaNeverTables.Contains(x.ClrType)), table =>
            Assert.Equal(ValueGenerated.Never, table.FindPrimaryKey()!.Properties.Single().ValueGenerated));
    }

    [Fact]
    public void Column_types_lengths_nullability_and_default_match_schema()
    {
        using var context = CreateContext();
        var news = context.Model.FindEntityType(typeof(NewsArticle))!;
        Assert.Equal(200, news.FindProperty(nameof(NewsArticle.NewsTitle))!.GetMaxLength());
        Assert.Equal("nvarchar(max)", news.FindProperty(nameof(NewsArticle.NewsContent))!.GetColumnType());
        Assert.Equal("tinyint", news.FindProperty(nameof(NewsArticle.NewsStatus))!.GetColumnType());
        Assert.Equal(3, news.FindProperty(nameof(NewsArticle.CreatedDate))!.GetPrecision());
        Assert.Equal("SYSUTCDATETIME()", news.FindProperty(nameof(NewsArticle.CreatedDate))!.GetDefaultValueSql());
        Assert.False(news.FindProperty(nameof(NewsArticle.CategoryId))!.IsNullable);
        Assert.False(news.FindProperty(nameof(NewsArticle.CreatedById))!.IsNullable);
        Assert.True(news.FindProperty(nameof(NewsArticle.UpdatedById))!.IsNullable);
        Assert.True(news.FindProperty(nameof(NewsArticle.ModifiedDate))!.IsNullable);
        var account = context.Model.FindEntityType(typeof(SystemAccount))!;
        Assert.Equal(254, account.FindProperty(nameof(SystemAccount.AccountEmail))!.GetMaxLength());
        Assert.Equal(512, account.FindProperty(nameof(SystemAccount.AccountPasswordHash))!.GetMaxLength());
        Assert.Equal(typeof(byte), account.FindProperty(nameof(SystemAccount.AccountRole))!.ClrType);
        Assert.False(account.FindProperty(nameof(SystemAccount.IsDeleted))!.IsNullable);
        Assert.Equal(false, account.FindProperty(nameof(SystemAccount.IsDeleted))!.GetDefaultValue());
        Assert.Null(account.GetQueryFilter()); // Keep deleted authors visible on News joins.
        var category = context.Model.FindEntityType(typeof(Category))!;
        Assert.Equal(500, category.FindProperty(nameof(Category.CategoryDescription))!.GetMaxLength());
        Assert.True(category.FindProperty(nameof(Category.CategoryDescription))!.IsNullable);
    }

    [Fact]
    public void Join_key_and_delete_behaviors_preserve_shared_records()
    {
        using var context = CreateContext();
        var join = context.Model.FindEntityType(typeof(NewsTag))!;
        Assert.Equal(new[] { "NewsArticleId", "TagId" }, join.FindPrimaryKey()!.Properties.Select(x => x.Name));
        var foreignKeys = context.Model.GetEntityTypes().SelectMany(x => x.GetForeignKeys()).ToArray();
        Assert.Equal(10, foreignKeys.Length);
        var cascades = foreignKeys.Where(x => x.DeleteBehavior == DeleteBehavior.Cascade).ToArray();
        Assert.Equal(4, cascades.Length);
        Assert.Contains(cascades, x => x.DeclaringEntityType.ClrType == typeof(VivaGradeChangeLog) && x.PrincipalEntityType.ClrType == typeof(VivaGradingResult));
        Assert.Contains(cascades, x => x.DeclaringEntityType.ClrType == typeof(VivaGradingResult) && x.PrincipalEntityType.ClrType == typeof(VivaSession));
        Assert.Contains(cascades, x => x.DeclaringEntityType.ClrType == typeof(NewsTag) && x.PrincipalEntityType.ClrType == typeof(NewsArticle));
        Assert.Contains(cascades, x => x.DeclaringEntityType.ClrType == typeof(VivaSessionTurn) && x.PrincipalEntityType.ClrType == typeof(VivaSession));
        Assert.All(foreignKeys.Where(x => !cascades.Contains(x)), x => Assert.Equal(DeleteBehavior.ClientNoAction, x.DeleteBehavior));
    }

    [Fact]
    public void Checks_and_descending_indexes_are_mapped()
    {
        using var context = CreateContext();
        var model = context.GetService<IDesignTimeModel>().Model;
        Assert.Equal(11, model.GetEntityTypes().SelectMany(x => x.GetCheckConstraints()).Count());
        var news = model.FindEntityType(typeof(NewsArticle))!;
        var history = Assert.Single(news.GetIndexes(), x => x.GetDatabaseName() == "IX_NewsArticle_CreatedById_CreatedDate");
        Assert.Equal(new[] { false, true }, history.IsDescending);
        var account = model.FindEntityType(typeof(SystemAccount))!;
        Assert.Equal("Latin1_General_100_CI_AS", account.FindProperty(nameof(SystemAccount.AccountEmail))!.GetCollation());
        Assert.True(Assert.Single(account.GetIndexes()).IsUnique);
    }

    [Fact]
    public void Viva_columns_match_sql_script()
    {
        using var context = CreateContext();
        var question = context.Model.FindEntityType(typeof(VivaQuestion))!;
        Assert.Equal("VivaQuestion", question.GetTableName());
        Assert.Equal(50, question.FindProperty(nameof(VivaQuestion.QuestionId))!.GetMaxLength());
        Assert.False(question.FindProperty(nameof(VivaQuestion.QuestionId))!.IsUnicode());
        Assert.Equal(50, question.FindProperty(nameof(VivaQuestion.ExamId))!.GetMaxLength());
        Assert.False(question.FindProperty(nameof(VivaQuestion.ExamId))!.IsUnicode());
        Assert.Equal(250, question.FindProperty(nameof(VivaQuestion.Title))!.GetMaxLength());
        Assert.Equal("nvarchar(max)", question.FindProperty(nameof(VivaQuestion.Content))!.GetColumnType());
        Assert.Equal(90, question.FindProperty(nameof(VivaQuestion.TimeLimitSeconds))!.GetDefaultValue());

        var session = context.Model.FindEntityType(typeof(VivaSession))!;
        Assert.Equal("VivaSession", session.GetTableName());
        Assert.Equal(50, session.FindProperty(nameof(VivaSession.ExamId))!.GetMaxLength());
        Assert.False(session.FindProperty(nameof(VivaSession.ExamId))!.IsUnicode());
        Assert.Equal(50, session.FindProperty(nameof(VivaSession.StudentId))!.GetMaxLength());
        Assert.Equal("tinyint", session.FindProperty(nameof(VivaSession.State))!.GetColumnType());
        Assert.Equal(3, session.FindProperty(nameof(VivaSession.StartedAtUtc))!.GetPrecision());

        var turn = context.Model.FindEntityType(typeof(VivaSessionTurn))!;
        Assert.Equal("VivaSessionTurn", turn.GetTableName());
        Assert.Equal(50, turn.FindProperty(nameof(VivaSessionTurn.QuestionId))!.GetMaxLength());
        Assert.False(turn.FindProperty(nameof(VivaSessionTurn.QuestionId))!.IsUnicode());
        Assert.Equal(5, turn.FindProperty(nameof(VivaSessionTurn.ConfidenceScore))!.GetPrecision());
        Assert.Equal(2, turn.FindProperty(nameof(VivaSessionTurn.ConfidenceScore))!.GetScale());
        Assert.Equal("nvarchar(max)", turn.FindProperty(nameof(VivaSessionTurn.TranscriptText))!.GetColumnType());
        Assert.Equal("nvarchar(max)", turn.FindProperty(nameof(VivaSessionTurn.FollowUpQuestionText))!.GetColumnType());
        Assert.Equal(3, turn.FindProperty(nameof(VivaSessionTurn.CreatedAtUtc))!.GetPrecision());
    }

    [Fact]
    public void VivaGradingResult_columns_match_schema()
    {
        using var context = CreateContext();
        var grading = context.Model.FindEntityType(typeof(VivaGradingResult))!;
        Assert.Equal("VivaGradingResult", grading.GetTableName());
        Assert.Equal(50, grading.FindProperty(nameof(VivaGradingResult.QuestionId))!.GetMaxLength());
        Assert.False(grading.FindProperty(nameof(VivaGradingResult.QuestionId))!.IsUnicode());
        Assert.Equal(5, grading.FindProperty(nameof(VivaGradingResult.AiScore))!.GetPrecision());
        Assert.Equal(2, grading.FindProperty(nameof(VivaGradingResult.AiScore))!.GetScale());
        Assert.Equal("nvarchar(max)", grading.FindProperty(nameof(VivaGradingResult.AiFeedback))!.GetColumnType());
        Assert.Equal(100, grading.FindProperty(nameof(VivaGradingResult.AiProvider))!.GetMaxLength());
        Assert.Equal(100, grading.FindProperty(nameof(VivaGradingResult.AiModel))!.GetMaxLength());
        Assert.Equal("tinyint", grading.FindProperty(nameof(VivaGradingResult.GradingState))!.GetColumnType());
        Assert.Equal(5, grading.FindProperty(nameof(VivaGradingResult.TeacherFinalScore))!.GetPrecision());
        Assert.Equal(2, grading.FindProperty(nameof(VivaGradingResult.TeacherFinalScore))!.GetScale());
        Assert.Equal(100, grading.FindProperty(nameof(VivaGradingResult.FinalizedBy))!.GetMaxLength());
        Assert.Equal(3, grading.FindProperty(nameof(VivaGradingResult.FinalizedAtUtc))!.GetPrecision());
        Assert.True(grading.FindProperty(nameof(VivaGradingResult.RowVersion))!.IsConcurrencyToken);
        var uniqueIndex = Assert.Single(grading.GetIndexes(), x => x.IsUnique);
        Assert.Equal("UQ_VivaGradingResult_SessionId_QuestionId", uniqueIndex.GetDatabaseName());
    }

    [Fact]
    public void VivaGradeChangeLog_columns_match_schema()
    {
        using var context = CreateContext();
        var log = context.Model.FindEntityType(typeof(VivaGradeChangeLog))!;
        Assert.Equal("VivaGradeChangeLog", log.GetTableName());
        Assert.Equal(5, log.FindProperty(nameof(VivaGradeChangeLog.OldScore))!.GetPrecision());
        Assert.Equal(2, log.FindProperty(nameof(VivaGradeChangeLog.OldScore))!.GetScale());
        Assert.True(log.FindProperty(nameof(VivaGradeChangeLog.OldScore))!.IsNullable);
        Assert.Equal(5, log.FindProperty(nameof(VivaGradeChangeLog.NewScore))!.GetPrecision());
        Assert.Equal(2, log.FindProperty(nameof(VivaGradeChangeLog.NewScore))!.GetScale());
        Assert.False(log.FindProperty(nameof(VivaGradeChangeLog.NewScore))!.IsNullable);
        Assert.Equal(100, log.FindProperty(nameof(VivaGradeChangeLog.ChangedBy))!.GetMaxLength());
        Assert.False(log.FindProperty(nameof(VivaGradeChangeLog.ChangedBy))!.IsNullable);
        Assert.Equal("nvarchar(max)", log.FindProperty(nameof(VivaGradeChangeLog.Reason))!.GetColumnType());
        Assert.False(log.FindProperty(nameof(VivaGradeChangeLog.Reason))!.IsNullable);
        Assert.Equal(3, log.FindProperty(nameof(VivaGradeChangeLog.ChangedAtUtc))!.GetPrecision());
        var designModel = context.GetService<IDesignTimeModel>().Model;
        var designLog = designModel.FindEntityType(typeof(VivaGradeChangeLog))!;
        Assert.Single(designLog.GetCheckConstraints(), c => c.Name == "CK_VivaGradeChangeLog_Reason_NotBlank");
        var historyIndex = Assert.Single(designLog.GetIndexes(), x => x.GetDatabaseName() == "IX_VivaGradeChangeLog_GradingResultId_ChangedAtUtc");
        Assert.Equal(new[] { false, true }, historyIndex.IsDescending);
    }

    [Fact]
    public void Email_can_change_without_becoming_an_immutable_key()
    {
        using var context = CreateContext();
        var account = new SystemAccount { AccountId = 1, AccountEmail = "before@example.test" };
        context.Attach(account);
        account.AccountEmail = "after@example.test";
        context.ChangeTracker.DetectChanges();
        Assert.True(context.Entry(account).Property(x => x.AccountEmail).IsModified);
        Assert.Equal(EntityState.Modified, context.Entry(account).State);
        // No SaveChanges: this test is entirely in memory.
    }

    [Fact]
    public void DbContext_registration_is_scoped()
    {
        var services = new ServiceCollection();
        services.AddAivesDataAccess("Server=unused;Database=ModelOnly;Integrated Security=True");
        Assert.Equal(ServiceLifetime.Scoped, Assert.Single(services, x => x.ServiceType == typeof(AIVESDbContext)).Lifetime);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        using var first = provider.CreateScope();
        using var second = provider.CreateScope();
        var context = first.ServiceProvider.GetRequiredService<AIVESDbContext>();
        Assert.Same(context, first.ServiceProvider.GetRequiredService<AIVESDbContext>());
        Assert.NotSame(context, second.ServiceProvider.GetRequiredService<AIVESDbContext>());
        foreach (var type in new[]
        {
            typeof(ISystemAccountRepository),
            typeof(ICategoryRepository),
            typeof(INewsArticleRepository),
            typeof(ITagRepository),
            typeof(IVivaSessionRepository),
            typeof(IVivaQuestionRepository),
            typeof(IVivaTurnRepository)
        })
        {
            Assert.Equal(ServiceLifetime.Scoped, Assert.Single(services, x => x.ServiceType == type).Lifetime);
            Assert.Same(first.ServiceProvider.GetRequiredService(type), first.ServiceProvider.GetRequiredService(type));
            Assert.NotSame(first.ServiceProvider.GetRequiredService(type), second.ServiceProvider.GetRequiredService(type));
        }
    }

    [Fact]
    public void Repositories_are_scoped_depend_directly_on_dbcontext_and_have_no_static_state()
    {
        var repoTypes = new[]
        {
            typeof(SystemAccountRepository),
            typeof(CategoryRepository),
            typeof(NewsArticleRepository),
            typeof(TagRepository),
            typeof(VivaSessionRepository),
            typeof(VivaQuestionRepository),
            typeof(VivaTurnRepository)
        };

        foreach (var type in repoTypes)
        {
            var constructors = type.GetConstructors();
            Assert.Single(constructors);
            var parameters = constructors[0].GetParameters();
            Assert.Contains(parameters, p => p.ParameterType == typeof(AIVESDbContext));

            var fields = type.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static);
            Assert.All(fields.Where(f => f.IsStatic), field =>
            {
                Assert.True(field.IsInitOnly, $"Static field {field.Name} on {type.Name} must be readonly.");
                Assert.False(typeof(DbContext).IsAssignableFrom(field.FieldType), $"Static field {field.Name} must not store DbContext.");
            });
        }
    }

    [Fact]
    public async Task Report_rejects_invalid_range_before_opening_a_connection()
    {
        using var context = CreateContext();
        var repository = new NewsArticleRepository(context);
        var utc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await Assert.ThrowsAsync<ArgumentException>(() => repository.GetByCreatedDateRangeAsync(utc, utc, default));
        await Assert.ThrowsAsync<ArgumentException>(() => repository.GetByCreatedDateRangeAsync(utc.AddDays(1), utc, default));
        await Assert.ThrowsAsync<ArgumentException>(() => repository.GetByCreatedDateRangeAsync(
            DateTime.SpecifyKind(utc, DateTimeKind.Unspecified), utc.AddDays(1), default));
    }
}
