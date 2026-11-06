using System.Reflection;
using AIVES.DAL.Context;
using AIVES.DAL.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AIVES.Tests.Data;

public class VivaRubricRepositoryTests
{
    private static void AssertProperty(Type type, string propertyName, Type expectedType)
    {
        var prop = type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
        Assert.True(prop != null, $"Property '{propertyName}' must exist on type '{type.FullName}'.");
        Assert.True(prop.PropertyType == expectedType,
            $"Property '{propertyName}' on '{type.FullName}' was expected to be {expectedType.Name} but was {prop.PropertyType.Name}.");
    }

    [Fact]
    public void VivaRubricCriterion_has_required_properties()
    {
        var type = Type.GetType("AIVES.DAL.Entities.VivaRubricCriterion, AIVES.DAL");
        Assert.True(type != null, "AIVES.DAL.Entities.VivaRubricCriterion entity must exist.");

        AssertProperty(type, "Id", typeof(string));
        AssertProperty(type, "QuestionId", typeof(string));
        AssertProperty(type, "CriterionCode", typeof(string));
        AssertProperty(type, "CriterionName", typeof(string));
        AssertProperty(type, "Description", typeof(string));
        AssertProperty(type, "Weight", typeof(decimal));
        AssertProperty(type, "MaxPoints", typeof(decimal));
        AssertProperty(type, "KeywordsJson", typeof(string));
    }

    [Fact]
    public void VivaQuestion_has_BloomLevel_property()
    {
        var prop = typeof(VivaQuestion).GetProperty("BloomLevel", BindingFlags.Public | BindingFlags.Instance);
        Assert.True(prop != null, "VivaQuestion must have a 'BloomLevel' property.");
        Assert.Equal(typeof(string), prop.PropertyType);
    }

    [Fact]
    public void AIVESDbContext_has_VivaRubricCriteria_DbSet()
    {
        var prop = typeof(AIVESDbContext).GetProperty("VivaRubricCriteria", BindingFlags.Public | BindingFlags.Instance);
        Assert.True(prop != null, "AIVESDbContext must define a DbSet property named 'VivaRubricCriteria'.");
    }

    [Fact]
    public void IVivaRubricRepository_defines_contract_methods()
    {
        var interfaceType = Type.GetType("AIVES.DAL.Repositories.Interfaces.IVivaRubricRepository, AIVES.DAL");
        Assert.True(interfaceType != null, "AIVES.DAL.Repositories.Interfaces.IVivaRubricRepository must exist.");
        Assert.True(interfaceType.IsInterface, "IVivaRubricRepository must be an interface.");

        var criterionType = Type.GetType("AIVES.DAL.Entities.VivaRubricCriterion, AIVES.DAL");
        Assert.True(criterionType != null, "VivaRubricCriterion must exist to verify IVivaRubricRepository return types.");

        var expectedListType = typeof(List<>).MakeGenericType(criterionType);
        var expectedTaskType = typeof(Task<>).MakeGenericType(expectedListType);

        var getByQuestion = interfaceType.GetMethod("GetCriteriaByQuestionAsync",
            new[] { typeof(string), typeof(CancellationToken) });
        Assert.True(getByQuestion != null,
            "IVivaRubricRepository must define GetCriteriaByQuestionAsync(string questionId, CancellationToken ct = default).");
        Assert.Equal(expectedTaskType, getByQuestion.ReturnType);

        var getByExam = interfaceType.GetMethod("GetCriteriaByExamAsync",
            new[] { typeof(string), typeof(CancellationToken) });
        Assert.True(getByExam != null,
            "IVivaRubricRepository must define GetCriteriaByExamAsync(string examId, CancellationToken ct = default).");
        Assert.Equal(expectedTaskType, getByExam.ReturnType);
    }

    [Fact]
    public void VivaRubricRepository_implements_IVivaRubricRepository()
    {
        var interfaceType = Type.GetType("AIVES.DAL.Repositories.Interfaces.IVivaRubricRepository, AIVES.DAL");
        var repoType = Type.GetType("AIVES.DAL.Repositories.Implementations.VivaRubricRepository, AIVES.DAL");

        Assert.True(interfaceType != null, "IVivaRubricRepository interface must exist.");
        Assert.True(repoType != null, "AIVES.DAL.Repositories.Implementations.VivaRubricRepository must exist.");
        Assert.True(interfaceType.IsAssignableFrom(repoType),
            "VivaRubricRepository must implement IVivaRubricRepository.");
    }

    [Fact]
    public async Task VivaRubricRepository_GetCriteriaByQuestionAsync_returns_criteria_for_question()
    {
        var repoType = Type.GetType("AIVES.DAL.Repositories.Implementations.VivaRubricRepository, AIVES.DAL");
        var criterionType = Type.GetType("AIVES.DAL.Entities.VivaRubricCriterion, AIVES.DAL");

        Assert.True(repoType != null, "VivaRubricRepository must exist to execute query test.");
        Assert.True(criterionType != null, "VivaRubricCriterion must exist to execute query test.");

        var options = new DbContextOptionsBuilder<AIVESDbContext>()
            .UseInMemoryDatabase(databaseName: "RubricRepo_ByQuestion_" + Guid.NewGuid())
            .Options;

        using var context = new AIVESDbContext(options);

        var q1 = new VivaQuestion { QuestionId = "EXAM01-Q01", ExamId = "EXAM01", Title = "Q1", Content = "C1" };
        var q2 = new VivaQuestion { QuestionId = "EXAM01-Q02", ExamId = "EXAM01", Title = "Q2", Content = "C2" };
        context.VivaQuestions.AddRange(q1, q2);

        var c1 = Activator.CreateInstance(criterionType)!;
        criterionType.GetProperty("Id")?.SetValue(c1, "RC01");
        criterionType.GetProperty("QuestionId")?.SetValue(c1, "EXAM01-Q01");
        criterionType.GetProperty("CriterionCode")?.SetValue(c1, "Q01-C1");
        criterionType.GetProperty("CriterionName")?.SetValue(c1, "Scope");
        criterionType.GetProperty("Description")?.SetValue(c1, "Scoped lifetime explanation");
        criterionType.GetProperty("Weight")?.SetValue(c1, 3.5m);
        criterionType.GetProperty("MaxPoints")?.SetValue(c1, 3.5m);
        criterionType.GetProperty("KeywordsJson")?.SetValue(c1, "[\"scoped\",\"request\"]");

        var c2 = Activator.CreateInstance(criterionType)!;
        criterionType.GetProperty("Id")?.SetValue(c2, "RC02");
        criterionType.GetProperty("QuestionId")?.SetValue(c2, "EXAM01-Q01");
        criterionType.GetProperty("CriterionCode")?.SetValue(c2, "Q01-C2");
        criterionType.GetProperty("CriterionName")?.SetValue(c2, "Tracker");
        criterionType.GetProperty("Weight")?.SetValue(c2, 3.5m);
        criterionType.GetProperty("MaxPoints")?.SetValue(c2, 3.5m);

        var c3 = Activator.CreateInstance(criterionType)!;
        criterionType.GetProperty("Id")?.SetValue(c3, "RC03");
        criterionType.GetProperty("QuestionId")?.SetValue(c3, "EXAM01-Q02");
        criterionType.GetProperty("CriterionCode")?.SetValue(c3, "Q02-C1");
        criterionType.GetProperty("CriterionName")?.SetValue(c3, "Separation");
        criterionType.GetProperty("Weight")?.SetValue(c3, 5.0m);
        criterionType.GetProperty("MaxPoints")?.SetValue(c3, 5.0m);

        context.AddRange(c1, c2, c3);
        await context.SaveChangesAsync();

        var repo = Activator.CreateInstance(repoType, context);
        Assert.NotNull(repo);

        var method = repoType.GetMethod("GetCriteriaByQuestionAsync", new[] { typeof(string), typeof(CancellationToken) });
        Assert.NotNull(method);

        var task = (Task)method.Invoke(repo, new object[] { "EXAM01-Q01", CancellationToken.None })!;
        await task;

        var result = task.GetType().GetProperty("Result")?.GetValue(task) as System.Collections.IEnumerable;
        Assert.NotNull(result);

        var list = result.Cast<object>().ToList();
        Assert.Equal(2, list.Count);
    }

    [Fact]
    public async Task VivaRubricRepository_GetCriteriaByExamAsync_returns_criteria_for_exam()
    {
        var repoType = Type.GetType("AIVES.DAL.Repositories.Implementations.VivaRubricRepository, AIVES.DAL");
        var criterionType = Type.GetType("AIVES.DAL.Entities.VivaRubricCriterion, AIVES.DAL");

        Assert.True(repoType != null, "VivaRubricRepository must exist to execute query test.");
        Assert.True(criterionType != null, "VivaRubricCriterion must exist to execute query test.");

        var options = new DbContextOptionsBuilder<AIVESDbContext>()
            .UseInMemoryDatabase(databaseName: "RubricRepo_ByExam_" + Guid.NewGuid())
            .Options;

        using var context = new AIVESDbContext(options);

        var q1 = new VivaQuestion { QuestionId = "EXAM01-Q01", ExamId = "EXAM01", Title = "Q1", Content = "C1" };
        var q2 = new VivaQuestion { QuestionId = "EXAM01-Q02", ExamId = "EXAM01", Title = "Q2", Content = "C2" };
        var q3 = new VivaQuestion { QuestionId = "EXAM02-Q01", ExamId = "EXAM02", Title = "Q3", Content = "C3" };
        context.VivaQuestions.AddRange(q1, q2, q3);

        var c1 = Activator.CreateInstance(criterionType)!;
        criterionType.GetProperty("Id")?.SetValue(c1, "RC01");
        criterionType.GetProperty("QuestionId")?.SetValue(c1, "EXAM01-Q01");
        criterionType.GetProperty("CriterionCode")?.SetValue(c1, "Q01-C1");
        criterionType.GetProperty("Weight")?.SetValue(c1, 3.5m);
        criterionType.GetProperty("MaxPoints")?.SetValue(c1, 3.5m);

        var c2 = Activator.CreateInstance(criterionType)!;
        criterionType.GetProperty("Id")?.SetValue(c2, "RC02");
        criterionType.GetProperty("QuestionId")?.SetValue(c2, "EXAM01-Q02");
        criterionType.GetProperty("CriterionCode")?.SetValue(c2, "Q02-C1");
        criterionType.GetProperty("Weight")?.SetValue(c2, 5.0m);
        criterionType.GetProperty("MaxPoints")?.SetValue(c2, 5.0m);

        var c3 = Activator.CreateInstance(criterionType)!;
        criterionType.GetProperty("Id")?.SetValue(c3, "RC03");
        criterionType.GetProperty("QuestionId")?.SetValue(c3, "EXAM02-Q01");
        criterionType.GetProperty("CriterionCode")?.SetValue(c3, "EX02-C1");
        criterionType.GetProperty("Weight")?.SetValue(c3, 10.0m);
        criterionType.GetProperty("MaxPoints")?.SetValue(c3, 10.0m);

        context.AddRange(c1, c2, c3);
        await context.SaveChangesAsync();

        var repo = Activator.CreateInstance(repoType, context);
        Assert.NotNull(repo);

        var method = repoType.GetMethod("GetCriteriaByExamAsync", new[] { typeof(string), typeof(CancellationToken) });
        Assert.NotNull(method);

        var task = (Task)method.Invoke(repo, new object[] { "EXAM01", CancellationToken.None })!;
        await task;

        var result = task.GetType().GetProperty("Result")?.GetValue(task) as System.Collections.IEnumerable;
        Assert.NotNull(result);

        var list = result.Cast<object>().ToList();
        Assert.Equal(2, list.Count);
    }
}
