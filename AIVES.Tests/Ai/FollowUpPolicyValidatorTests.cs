using System;
using Xunit;
using AIVES.BLL.Interfaces.Ai;
using AIVES.BLL.Services.Ai;

namespace AIVES.Tests.Ai;

public class FollowUpPolicyValidatorTests
{
    private readonly FollowUpPolicyValidator _validator = new();

    #region Valid Input — Pass Through

    [Fact]
    public void Validate_Returns_Original_When_Question_Is_Valid()
    {
        var result = new FollowUpResult(
            IsSuccess: true,
            FollowUpQuestionText: "Why would injecting a Scoped DbContext into a Singleton service cause concurrency issues?",
            CognitiveIntent: "Deep-Dive"
        );

        var validated = _validator.Validate(result);

        Assert.True(validated.IsSuccess);
        Assert.Equal(result.FollowUpQuestionText, validated.FollowUpQuestionText);
        Assert.Equal("Deep-Dive", validated.CognitiveIntent);
    }

    [Fact]
    public void Validate_Accepts_All_Allowed_Cognitive_Intents()
    {
        string[] allowedIntents = ["Clarification", "Deep-Dive", "Edge-Case", "Counter-Example"];

        foreach (var intent in allowedIntents)
        {
            var result = new FollowUpResult(
                IsSuccess: true,
                FollowUpQuestionText: "Can you elaborate on the thread safety implications of your approach?",
                CognitiveIntent: intent
            );

            var validated = _validator.Validate(result);

            Assert.True(validated.IsSuccess, $"Intent '{intent}' should be valid.");
            Assert.Equal(intent, validated.CognitiveIntent);
        }
    }

    #endregion

    #region Failure / Empty Result — Fallback Substitution

    [Fact]
    public void Validate_Substitutes_Fallback_When_Result_IsSuccess_Is_False()
    {
        var failed = new FollowUpResult(false, string.Empty, string.Empty, "LLM timeout");

        var validated = _validator.Validate(failed, "Tell me about DbContext", "It manages database connections.");

        Assert.True(validated.IsSuccess);
        Assert.NotEmpty(validated.FollowUpQuestionText);
        Assert.Null(validated.ErrorMessage);
    }

    [Fact]
    public void Validate_Substitutes_Fallback_When_Question_Is_Null()
    {
        var result = new FollowUpResult(true, null!, "Deep-Dive");

        var validated = _validator.Validate(result);

        Assert.True(validated.IsSuccess);
        Assert.NotEmpty(validated.FollowUpQuestionText);
    }

    [Fact]
    public void Validate_Substitutes_Fallback_When_Question_Is_Empty_String()
    {
        var result = new FollowUpResult(true, "   ", "Deep-Dive");

        var validated = _validator.Validate(result);

        Assert.True(validated.IsSuccess);
        Assert.NotEmpty(validated.FollowUpQuestionText);
    }

    #endregion

    #region Length Bounds

    [Fact]
    public void Validate_Substitutes_Fallback_When_Question_Is_Too_Short()
    {
        var result = new FollowUpResult(true, "Why?", "Clarification");

        var validated = _validator.Validate(result);

        Assert.True(validated.IsSuccess);
        Assert.True(validated.FollowUpQuestionText.Length > 4,
            "Fallback should be longer than the too-short input.");
    }

    [Fact]
    public void Validate_Substitutes_Fallback_When_Question_Exceeds_Max_Length()
    {
        var tooLong = new string('A', 501);
        var result = new FollowUpResult(true, tooLong, "Deep-Dive");

        var validated = _validator.Validate(result);

        Assert.True(validated.IsSuccess);
        Assert.True(validated.FollowUpQuestionText.Length <= 500,
            "Validated question length should not exceed 500 chars.");
        Assert.NotEqual(tooLong, validated.FollowUpQuestionText);
    }

    #endregion

    #region Prohibited Content

    [Theory]
    [InlineData("Your score is 8 out of 10 for that answer.")]
    [InlineData("You have passed this question successfully.")]
    [InlineData("That answer was wrong, please try again.")]
    [InlineData("Your grade for this response is B+.")]
    [InlineData("Excellent answer! You've given a correct explanation.")]
    [InlineData("I am an AI language model and I cannot evaluate.")]
    public void Validate_Substitutes_Fallback_When_Question_Contains_Prohibited_Content(string prohibitedText)
    {
        var result = new FollowUpResult(true, prohibitedText, "Deep-Dive");

        var validated = _validator.Validate(result);

        Assert.True(validated.IsSuccess);
        Assert.NotEqual(prohibitedText, validated.FollowUpQuestionText);
    }

    #endregion

    #region Cognitive Intent Normalization

    [Theory]
    [InlineData("unknown")]
    [InlineData("scoring")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("   ")]
    public void Validate_Normalizes_Unknown_CognitiveIntent_To_DeepDive(string? badIntent)
    {
        var result = new FollowUpResult(
            IsSuccess: true,
            FollowUpQuestionText: "How does the Repository pattern isolate business rules from the database?",
            CognitiveIntent: badIntent!
        );

        var validated = _validator.Validate(result);

        Assert.True(validated.IsSuccess);
        // Either the question was replaced (fallback with Clarification) or intent was corrected to Deep-Dive
        Assert.True(
            validated.CognitiveIntent == "Deep-Dive" || validated.CognitiveIntent == "Clarification",
            $"Intent should be 'Deep-Dive' or 'Clarification', was: '{validated.CognitiveIntent}'");
    }

    #endregion

    #region Topic-specific Fallback Selection

    [Theory]
    [InlineData("Explain DbContext lifetime", "It creates a new instance per request")]
    [InlineData("What is SignalR?", "It enables realtime WebSocket communication via Hub")]
    [InlineData("Explain async/await pattern", "We use async to avoid blocking the main thread")]
    [InlineData("Describe Repository pattern", "The repository abstracts the DAL layer")]
    public void Validate_Selects_Topic_Relevant_Fallback_Question(
        string originalQuestion,
        string transcript)
    {
        var failed = new FollowUpResult(false, string.Empty, string.Empty, "timeout");

        var validated = _validator.Validate(failed, originalQuestion, transcript);

        Assert.True(validated.IsSuccess);
        Assert.NotEmpty(validated.FollowUpQuestionText);
        Assert.True(validated.FollowUpQuestionText.Length > 10);
    }

    [Fact]
    public void Validate_Returns_Universal_Fallback_When_No_Topic_Matched()
    {
        var failed = new FollowUpResult(false, string.Empty, string.Empty, "timeout");

        var validated = _validator.Validate(failed, "Random unrelated question", "Random unrelated answer");

        Assert.True(validated.IsSuccess);
        Assert.NotEmpty(validated.FollowUpQuestionText);
    }

    #endregion

    #region Valid Edge Cases

    [Fact]
    public void Validate_Accepts_Question_At_Max_Boundary()
    {
        // Exactly 500 chars should pass
        var question = new string('A', 495) + "?????";
        var result = new FollowUpResult(true, question, "Deep-Dive");

        var validated = _validator.Validate(result);

        Assert.True(validated.IsSuccess);
        Assert.Equal(question, validated.FollowUpQuestionText);
    }

    [Fact]
    public void Validate_Accepts_Question_At_Min_Boundary()
    {
        // Exactly 10 chars should pass
        var question = "Why async?";
        var result = new FollowUpResult(true, question, "Clarification");

        var validated = _validator.Validate(result);

        Assert.True(validated.IsSuccess);
        Assert.Equal(question, validated.FollowUpQuestionText);
    }

    #endregion
}
