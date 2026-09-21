using Workspace.Server;

namespace Workspace.Server.Tests;

public sealed class VisualClarificationTests
{
    [Fact]
    public void AllVisualInputsValidateAndTheirTypedAnswersRoundTrip()
    {
        var spec = ServiceTestData.VisualQuestions();
        SpecValidator.Validate(spec, "clarification");
        var answers = SpecValidator.ValidateAnswers(spec, ServiceTestData.AnswersFor(spec));
        Assert.Equal("3", answers["detail"]);
        Assert.Equal("[\"product\",\"marketing\"]", answers["stakeholders"]);
        Assert.Equal("0", answers["budget"]);
        Assert.Equal("false", answers["review"]);
        Assert.Equal("2026-09-20", answers["deadline"]);
        Assert.Throws<WorkspaceException>(() => SpecValidator.Validate(spec, "artifact"));
    }

    [Theory]
    [InlineData("detail", "0")]
    [InlineData("detail", "6")]
    [InlineData("detail", "2.5")]
    [InlineData("detail", "NaN")]
    [InlineData("budget", "0.25")]
    [InlineData("budget", "-1")]
    [InlineData("budget", "10000.5")]
    [InlineData("budget", "Infinity")]
    [InlineData("review", "yes")]
    [InlineData("deadline", "2026-02-30")]
    [InlineData("deadline", "2026-09-19")]
    [InlineData("deadline", "2027-01-01")]
    [InlineData("deadline", "2026-9-20")]
    [InlineData("stakeholders", "product,marketing")]
    [InlineData("stakeholders", "[\"unknown\"]")]
    [InlineData("stakeholders", "[\"product\",\"product\"]")]
    [InlineData("stakeholders", "[]")]
    [InlineData("stakeholders", "[null]")]
    public void InvalidTypedAnswersAreRejectedWithoutChangingTheForm(string id, string value)
    {
        var spec = ServiceTestData.VisualQuestions();
        var answers = ServiceTestData.AnswersFor(spec);
        answers[id] = value;
        var error = Assert.Throws<WorkspaceException>(() => SpecValidator.ValidateAnswers(spec, answers));
        Assert.Equal("invalid_answers", error.Code);
    }

    [Theory]
    [InlineData("bounds")]
    [InlineData("zero-step")]
    [InlineData("unaligned-default")]
    [InlineData("wrong-label-count")]
    [InlineData("too-many-steps")]
    [InlineData("invalid-date-default")]
    [InlineData("invalid-date-bounds")]
    [InlineData("invalid-choice-default")]
    [InlineData("string-toggle")]
    public void InvalidControlDefinitionsCannotBecomeGeneratedUi(string variation)
    {
        var spec = ServiceTestData.VisualQuestions();
        var slider = spec.Elements["detail"].Props;
        switch (variation)
        {
            case "bounds": slider["max"] = 0; break;
            case "zero-step": slider["step"] = 0; break;
            case "unaligned-default": slider["value"] = 2.5; break;
            case "wrong-label-count": slider["labels"]!.AsArray().RemoveAt(0); break;
            case "too-many-steps": slider["step"] = 0.000001; break;
            case "invalid-date-default": spec.Elements["deadline"].Props["value"] = "2026-02-30"; break;
            case "invalid-date-bounds": spec.Elements["deadline"].Props["min"] = "2027-01-01"; break;
            case "invalid-choice-default": spec.Elements["stakeholders"].Props["value"] = ServiceTestData.Props(new { bad = true }); break;
            case "string-toggle": spec.Elements["review"].Props["value"] = "false"; break;
        }
        Assert.Throws<WorkspaceException>(() => SpecValidator.Validate(spec, "clarification"));
    }

    [Fact]
    public void DecimalSlidersAndOptionalEmptyValuesAreSupportedWithoutInventingAnswers()
    {
        var spec = ServiceTestData.VisualQuestions();
        var slider = spec.Elements["detail"].Props;
        slider["min"] = 0;
        slider["max"] = 1;
        slider["step"] = 0.1;
        slider["value"] = 0;
        slider["labels"] = null;
        var answers = ServiceTestData.AnswersFor(spec);
        answers["detail"] = "0.3";
        answers["deadline"] = "";
        answers.Remove("budget");
        var validated = SpecValidator.ValidateAnswers(spec, answers);
        Assert.Equal("0.3", validated["detail"]);
        Assert.Equal("", validated["deadline"]);
        Assert.Equal("", validated["budget"]);
    }

    [Fact]
    public void RefreshIsExplicitAndKeepsTheCurrentFormAndDraftContractUntilSuccess()
    {
        using var fixture = new ServiceFixture();
        var workspace = ServiceTestData.Seed(fixture.Store);
        fixture.Store.Update(workspace.Id, "test.questions", "Questions.", document =>
        {
            document.ClarificationId = "saved-form";
            document.Clarification = ServiceTestData.VisualQuestions();
        });
        var refreshed = fixture.Service.RefreshClarification(workspace.Id, new("saved-form"));
        Assert.Equal("clarify", refreshed.Jobs.Last().Kind);
        Assert.Equal("saved-form", refreshed.ClarificationId);
        Assert.NotNull(refreshed.Clarification);
        Assert.Throws<WorkspaceException>(() => fixture.Service.Answer(workspace.Id,
            new("saved-form", ServiceTestData.AnswersFor(refreshed.Clarification!))));
        Assert.Throws<WorkspaceException>(() => fixture.Service.RefreshClarification(workspace.Id, new("saved-form")));
        fixture.Service.Cancel(workspace.Id, refreshed.Jobs.Last().Id);
        var answered = fixture.Service.Answer(workspace.Id,
            new("saved-form", ServiceTestData.AnswersFor(refreshed.Clarification!)));
        Assert.Null(answered.Clarification);
        Assert.Equal("3", answered.Answers["detail"]);
    }

    [Fact]
    public void VisualAnswersPersistAcrossServiceRestartWithoutLosingZeroFalseOrMultipleSelections()
    {
        using var fixture = new ServiceFixture();
        var workspace = ServiceTestData.Seed(fixture.Store);
        fixture.Store.Update(workspace.Id, "test.questions", "Visual decisions.", document =>
        {
            document.ClarificationId = "visual-form";
            document.Clarification = ServiceTestData.VisualQuestions();
        });
        fixture.Service.Answer(workspace.Id,
            new("visual-form", ServiceTestData.AnswersFor(ServiceTestData.VisualQuestions())));
        fixture.Reopen();
        var restored = fixture.Store.Get(workspace.Id);
        Assert.Equal("0", restored.Answers["budget"]);
        Assert.Equal("false", restored.Answers["review"]);
        Assert.Equal("[\"product\",\"marketing\"]", restored.Answers["stakeholders"]);
        Assert.Equal("2026-09-20", restored.Answers["deadline"]);
    }
}
