using System.Globalization;
using System.Text.Json;
using Workspace.Server;
using Workspace.Server.Providers;

namespace Workspace.Server.Tests;

public sealed class DemoProviderTests
{
    [Fact]
    public async Task SimpleCompleteTaskIsDraftedByTheCoordinatorWithoutSpecialists()
    {
        var reply = await Provider().CompleteAsync(Request("planner", WorkflowTestData.Prompt(
            objective: "Write a one-sentence thank-you note to the team.", kind: "initial", clarificationAllowed: true)),
            _ => Task.CompletedTask, CancellationToken.None);
        var plan = JsonSerializer.Deserialize<PlannerResponse>(reply.Text, JsonDefaults.Options)!;
        Assert.Null(plan.Clarification);
        Assert.Empty(plan.Agents!);
        var draft = Assert.Single(plan.Artifacts!);
        SpecValidator.Validate(draft.Spec, "artifact");
        Assert.Contains("Thank you, team", WorkflowTestData.Json(draft.Spec));
    }

    [Theory]
    [InlineData("Write one friendly sentence thanking Jordan for reviewing a prototype. No research or independent review is needed.", false)]
    [InlineData("Write a single concise sentence thanking the team, without an additional review.", false)]
    [InlineData("Write one short sentence thanking Jordan and request an independent review.", true)]
    public async Task SmallTaskAdjectivesAndNegatedExtrasDoNotForceUnnecessaryAgents(string objective, bool review)
    {
        var reply = await Provider().CompleteAsync(Request("planner", WorkflowTestData.Prompt(
            objective: objective, kind: "initial", clarificationAllowed: true)),
            _ => Task.CompletedTask, CancellationToken.None);
        var plan = JsonSerializer.Deserialize<PlannerResponse>(reply.Text, JsonDefaults.Options)!;
        Assert.Null(plan.Clarification);
        Assert.Single(plan.Artifacts!);
        if (review) Assert.Equal("review", Assert.Single(plan.Agents!).Kind);
        else Assert.Empty(plan.Agents!);
    }

    [Fact]
    public async Task NegatedResearchDoesNotMisclassifyALaunchAsAResearchTask()
    {
        var reply = await Provider().CompleteAsync(Request("planner", WorkflowTestData.Prompt(
            objective: "Plan an internal product launch with no external research or independent review.",
            answers: new() { ["detail"] = "3", ["stakeholders"] = "[\"product\"]" })),
            _ => Task.CompletedTask, CancellationToken.None);
        var plan = JsonSerializer.Deserialize<PlannerResponse>(reply.Text, JsonDefaults.Options)!;
        var specialist = Assert.Single(plan.Agents!);
        Assert.Equal("launch-strategist", specialist.Id);
        Assert.Equal("produce", specialist.Kind);
    }

    [Fact]
    public async Task ACompleteCommunicationTaskNeedsNeitherClarificationNorAnAutomaticReviewer()
    {
        var reply = await Provider().CompleteAsync(Request("planner", WorkflowTestData.Prompt(
            objective: "Draft a short email to the team with no review.", kind: "initial", clarificationAllowed: true)),
            _ => Task.CompletedTask, CancellationToken.None);
        var plan = JsonSerializer.Deserialize<PlannerResponse>(reply.Text, JsonDefaults.Options)!;
        Assert.Null(plan.Clarification);
        var agent = Assert.Single(plan.Agents!);
        Assert.Equal("communications-writer", agent.Id);
        Assert.Equal("produce", agent.Kind);
    }

    [Theory]
    [InlineData("1", "false", 1, 0)]
    [InlineData("5", "false", 2, 0)]
    [InlineData("5", "true", 3, 1)]
    public async Task ConfirmedScopeAndReviewPreferenceChooseTheTeam(
        string detail, string review, int expectedAgents, int expectedReviewers)
    {
        var reply = await Provider().CompleteAsync(Request("planner", WorkflowTestData.Prompt(
            objective: "Plan a product launch", answers: new()
            {
                ["detail"] = detail, ["stakeholders"] = "[\"product\",\"marketing\"]", ["review"] = review
            })), _ => Task.CompletedTask, CancellationToken.None);
        var plan = JsonSerializer.Deserialize<PlannerResponse>(reply.Text, JsonDefaults.Options)!;
        AgentPlanPolicy.ValidateAssignments(plan.Agents);
        Assert.Null(plan.Clarification);
        Assert.Equal(expectedAgents, plan.Agents!.Count);
        Assert.Equal(expectedReviewers, plan.Agents.Count(agent => agent.Kind == "review"));
        Assert.Equal("launch-strategist", plan.Agents[0].Id);
        Assert.DoesNotContain(plan.Agents, agent => agent.Id is "writer" or "reviewer");
    }

    [Fact]
    public async Task DifferentTasksSelectDifferentSpecialistIdentitiesAndPurposes()
    {
        var provider = Provider();
        var launch = await provider.CompleteAsync(Request("planner", WorkflowTestData.Prompt(
            objective: "Plan a product launch", answers: new() { ["review"] = "false" })),
            _ => Task.CompletedTask, CancellationToken.None);
        var research = await provider.CompleteAsync(Request("planner", WorkflowTestData.Prompt(
            objective: "Compare options for a community project", answers: new() { ["review"] = "false" })),
            _ => Task.CompletedTask, CancellationToken.None);
        var first = JsonSerializer.Deserialize<PlannerResponse>(launch.Text, JsonDefaults.Options)!.Agents![0];
        var second = JsonSerializer.Deserialize<PlannerResponse>(research.Text, JsonDefaults.Options)!.Agents![0];
        Assert.NotEqual(first.Id, second.Id);
        Assert.NotEqual(first.Name, second.Name);
        Assert.NotEqual(first.Role, second.Role);
    }

    [Fact]
    public async Task AnAppropriateInactiveIdentityIsReusedInsteadOfRecreated()
    {
        var reply = await Provider().CompleteAsync(Request("planner", WorkflowTestData.Prompt(
            objective: "Plan a product launch",
            existingAgents:
            [
                new { id = "saved-launch-lead", name = "Launch strategist", role = "Earlier launch planning.", assigned = false }
            ])), _ => Task.CompletedTask, CancellationToken.None);
        var plan = JsonSerializer.Deserialize<PlannerResponse>(reply.Text, JsonDefaults.Options)!;
        Assert.Equal("saved-launch-lead", Assert.Single(plan.Agents!).Id);
    }

    [Fact]
    public async Task RichDefaultFormUsesTheMockStyleSliderAndStakeholderChipsWithConfirmableDefaults()
    {
        var objective = "Plan the launch of a new collaboration product";
        var reply = await Provider().CompleteAsync(Request("planner", WorkflowTestData.Prompt(
            objective: objective, kind: "initial", clarificationAllowed: true)), _ => Task.CompletedTask, CancellationToken.None);
        var plan = JsonSerializer.Deserialize<PlannerResponse>(reply.Text, JsonDefaults.Options)!;
        var form = Assert.IsType<UiSpec>(plan.Clarification);
        SpecValidator.Validate(form, "clarification");
        Assert.Empty(plan.Agents!);
        Assert.Empty(plan.Artifacts!);
        var slider = Assert.Single(form.Elements.Values, element => element.Type == "SliderQuestion");
        Assert.Equal(1, slider.Props["min"]!.GetValue<int>());
        Assert.Equal(5, slider.Props["max"]!.GetValue<int>());
        Assert.Equal(1, slider.Props["step"]!.GetValue<int>());
        Assert.Equal("Executive Brief", slider.Props["labels"]![0]!.GetValue<string>());
        Assert.Equal("Full Playbook", slider.Props["labels"]![4]!.GetValue<string>());
        Assert.Single(form.Elements.Values, element => element.Type == "MultiChoiceQuestion");
        Assert.Single(form.Elements.Values, element => element.Type == "ToggleQuestion");
        Assert.DoesNotContain(form.Elements.Values, element => element.Type == "TextQuestion");
        Assert.DoesNotContain(objective, WorkflowTestData.Json(form));

        var defaults = form.Elements.Where(item => item.Value.Type.EndsWith("Question", StringComparison.Ordinal))
            .ToDictionary(item => item.Key, item => DefaultAnswer(item.Value));
        var validated = SpecValidator.ValidateAnswers(form, defaults);
        Assert.Equal("3", validated["detail"]);
        Assert.Equal("[\"product\",\"marketing\"]", validated["stakeholders"]);
        Assert.Equal("true", validated["review"]);
    }

    [Fact]
    public async Task DatesAndBudgetsUseTheirSemanticControlsWithoutRepeatingKnownScopeOrTeams()
    {
        var objective = "Plan a detailed workshop for Product and Marketing with a deadline and a budget.";
        var reply = await Provider().CompleteAsync(Request("planner", WorkflowTestData.Prompt(
            objective: objective, kind: "initial", clarificationAllowed: true)), _ => Task.CompletedTask, CancellationToken.None);
        var plan = JsonSerializer.Deserialize<PlannerResponse>(reply.Text, JsonDefaults.Options)!;
        var form = Assert.IsType<UiSpec>(plan.Clarification);
        Assert.Single(form.Elements.Values, element => element.Type == "DateQuestion");
        Assert.Single(form.Elements.Values, element => element.Type == "NumberQuestion");
        Assert.DoesNotContain(form.Elements.Values, element => element.Type is "TextQuestion" or "SliderQuestion" or "MultiChoiceQuestion");
        var answers = SpecValidator.ValidateAnswers(form, new()
        {
            ["deadline"] = "2026-11-20", ["budget"] = "2500", ["review"] = "false"
        });
        var continued = await Provider().CompleteAsync(Request("planner",
            WorkflowTestData.Prompt(objective: objective, answers: answers)), _ => Task.CompletedTask, CancellationToken.None);
        var team = JsonSerializer.Deserialize<PlannerResponse>(continued.Text, JsonDefaults.Options)!;
        Assert.Null(team.Clarification);
        Assert.Equal("event-planner", team.Agents![0].Id);
        Assert.DoesNotContain(team.Agents, agent => agent.Kind == "review");
    }

    [Fact]
    public async Task ADeadlineInTheObjectiveDoesNotPretendToAnswerAnUnknownPeopleCount()
    {
        var reply = await Provider().CompleteAsync(Request("planner", WorkflowTestData.Prompt(
            objective: "Plan a brief workshop for participants by 2026-11-20 with no review.",
            kind: "initial", clarificationAllowed: true)), _ => Task.CompletedTask, CancellationToken.None);
        var form = JsonSerializer.Deserialize<PlannerResponse>(reply.Text, JsonDefaults.Options)!.Clarification!;
        Assert.Contains(form.Elements, item => item.Key == "quantity" && item.Value.Type == "NumberQuestion");
        Assert.DoesNotContain(form.Elements.Values, element => element.Type == "DateQuestion");
        Assert.DoesNotContain(form.Elements.Values, element => element.Type == "ToggleQuestion");
    }

    [Theory]
    [InlineData("detail", "2.5")]
    [InlineData("detail", "6")]
    [InlineData("detail", "NaN")]
    [InlineData("stakeholders", "product,marketing")]
    [InlineData("stakeholders", "[\"product\",\"product\"]")]
    [InlineData("stakeholders", "[\"unknown\"]")]
    [InlineData("review", "yes")]
    [InlineData("budget", "2501")]
    [InlineData("quantity", "2.5")]
    [InlineData("deadline", "2026-99-20")]
    public async Task MalformedTypedAnswersFailRatherThanBecomingImplicitDecisions(string key, string value)
    {
        var error = await Assert.ThrowsAsync<AgentProviderException>(() => Provider().CompleteAsync(
            Request("planner", WorkflowTestData.Prompt(answers: new() { [key] = value })),
            _ => Task.CompletedTask, CancellationToken.None));
        Assert.Equal("invalid_request", error.Code);
    }

    [Fact]
    public async Task TypedAnswersBecomeReadableArtifactContentInsteadOfRawControlState()
    {
        var reply = await Provider().CompleteAsync(Request("producer", WorkflowTestData.Prompt(
            assignment: Assignment("producer"), answers: new()
            {
                ["detail"] = "5", ["stakeholders"] = "[\"product\",\"marketing\"]", ["review"] = "false",
                ["quantity"] = "25", ["budget"] = "2500", ["deadline"] = "2026-11-20"
            })), _ => Task.CompletedTask, CancellationToken.None);
        var artifact = JsonSerializer.Deserialize<ProducerResponse>(reply.Text, JsonDefaults.Options)!.Artifacts[0];
        SpecValidator.Validate(artifact.Spec, "artifact");
        var text = WorkflowTestData.Json(artifact.Spec);
        Assert.Contains("Full Playbook", text);
        Assert.Contains("Product, Marketing", text);
        Assert.Contains("No specialist review requested", text);
        Assert.Contains("2026-11-20", text);
        Assert.Contains("2500", text);
    }

    [Fact]
    public async Task ExplicitRefreshModernizesALegacyTextFormWithoutRecruitingAgents()
    {
        var old = WorkflowTestData.Form(6, openText: true, required: true);
        var before = WorkflowTestData.Json(old);
        var reply = await Provider().CompleteAsync(Request("planner", WorkflowTestData.Prompt(
            objective: "Plan a product launch", kind: "clarify", clarificationAllowed: true, clarification: old)),
            _ => Task.CompletedTask, CancellationToken.None);
        var plan = JsonSerializer.Deserialize<PlannerResponse>(reply.Text, JsonDefaults.Options)!;
        var form = Assert.IsType<UiSpec>(plan.Clarification);
        SpecValidator.Validate(form, "clarification");
        Assert.InRange(form.Elements.Values.Count(element => element.Type.EndsWith("Question", StringComparison.Ordinal)), 2, 4);
        Assert.DoesNotContain(form.Elements.Values, element => element.Type == "TextQuestion");
        Assert.Empty(plan.Agents!);
        Assert.Empty(plan.Artifacts!);
        Assert.Equal(before, WorkflowTestData.Json(old));
    }

    [Fact]
    public async Task StatusExplicitlyNamesTheNonModelDemo()
    {
        var provider = Provider();
        var status = await provider.GetStatusAsync(CancellationToken.None);
        Assert.Equal("demo", provider.Name);
        Assert.Equal("demo", status.Id);
        Assert.Equal("demo", status.Model);
        Assert.Null(status.ReasoningEffort);
        Assert.Equal("ready", status.State);
        Assert.Contains("No model calls", status.Message);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(5001)]
    public void DelayIsBoundedAndInvalidConfigurationIsNotSilentlyClamped(int delay) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Provider(delay));

    [Fact]
    public async Task OutputsAndRoleSessionsAreDeterministicDistinctAndResumable()
    {
        var provider = Provider();
        var sessions = new List<string>();
        foreach (var (agent, kind) in new[] { ("planner", "planner"), ("writer", "producer"), ("reviewer", "reviewer") })
        {
            var request = Request(kind, agent: agent);
            var signals = new List<AgentSignal>();
            var first = await provider.CompleteAsync(request, signal =>
            {
                signals.Add(signal);
                return Task.CompletedTask;
            }, CancellationToken.None);
            var resumed = await provider.CompleteAsync(request with { SessionId = first.SessionId }, _ => Task.CompletedTask, CancellationToken.None);
            Assert.Equal(first, resumed);
            Assert.Equal(first.SessionId, signals.Single(signal => signal.Kind == "session").SessionId);
            sessions.Add(first.SessionId);
        }
        Assert.Equal(3, sessions.Distinct().Count());
        var another = await provider.CompleteAsync(Request("planner", workspace: "workspace-two"), _ => Task.CompletedTask, CancellationToken.None);
        Assert.DoesNotContain(another.SessionId, sessions);
    }

    [Fact]
    public async Task InitialClarificationIsValidAndLaterAnswersDoNotRepeatIt()
    {
        var provider = Provider();
        var initial = await provider.CompleteAsync(Request("planner",
            WorkflowTestData.Prompt(kind: "initial", clarificationAllowed: true)), _ => Task.CompletedTask, CancellationToken.None);
        var questions = JsonSerializer.Deserialize<PlannerResponse>(initial.Text, JsonDefaults.Options)!;
        SpecValidator.Validate(questions.Clarification, "clarification");
        Assert.Contains(questions.Clarification!.Elements.Values, element => element.Type == "SliderQuestion");
        Assert.Contains(questions.Clarification.Elements.Values, element => element.Type == "MultiChoiceQuestion");
        Assert.Equal(3, questions.Clarification.Elements.Values.Count(element => element.Type.EndsWith("Question")));
        Assert.Empty(questions.Agents!);
        Assert.Empty(questions.Artifacts!);

        var later = await provider.CompleteAsync(Request("planner", WorkflowTestData.Prompt(
            answers: new() { ["audience"] = "leadership" })), _ => Task.CompletedTask, CancellationToken.None);
        var plan = JsonSerializer.Deserialize<PlannerResponse>(later.Text, JsonDefaults.Options)!;
        Assert.Null(plan.Clarification);
        Assert.Single(plan.Agents!);
        Assert.Equal("produce", plan.Agents![0].Kind);
    }

    [Fact]
    public async Task SampleArtifactUsesRealWireComponentsAndSavedTaskAnswers()
    {
        var reply = await Provider().CompleteAsync(Request("producer", WorkflowTestData.Prompt(
            objective: "Design a volunteer onboarding plan",
            answers: new() { ["audience"] = "Volunteer coordinators", ["constraints"] = "Keep it accessible and low-cost." },
            assignment: Assignment("producer"))),
            _ => Task.CompletedTask, CancellationToken.None);
        var response = JsonSerializer.Deserialize<ProducerResponse>(reply.Text, JsonDefaults.Options)!;
        var artifact = Assert.Single(response.Artifacts);
        Assert.Null(artifact.ArtifactId);
        SpecValidator.Validate(artifact.Spec, "artifact");
        var types = artifact.Spec.Elements.Values.Select(element => element.Type).ToHashSet();
        Assert.Subset(types, new HashSet<string> { "Text", "BulletList", "DataTable", "Decision" });
        Assert.Contains("Design a volunteer onboarding plan", reply.Text);
        Assert.Contains("Volunteer coordinators", reply.Text);
        Assert.Contains("accessible and low-cost", reply.Text);
        Assert.DoesNotContain("acceptedRevision", reply.Text);
    }

    [Theory]
    [InlineData("objective")]
    [InlineData("task-heading")]
    [InlineData("next-steps")]
    [InlineData("delivery-checklist")]
    [InlineData("approach")]
    [InlineData("document")]
    public async Task FeedbackRefinesItsLatestTargetWithoutMutatingTheSnapshotOrChangingIds(string elementId)
    {
        var provider = Provider();
        var first = await provider.CompleteAsync(Request("producer"), _ => Task.CompletedTask, CancellationToken.None);
        var spec = JsonSerializer.Deserialize<ProducerResponse>(first.Text, JsonDefaults.Options)!.Artifacts[0].Spec;
        spec.Elements["objective"].Props["text"] = "Latest human-edited objective";
        var original = WorkflowTestData.Json(spec);
        var feedback = new
        {
            text = "Use everyday language", artifactId = "existing-artifact", elementId, revision = 1,
            targetLabel = "Existing draft", quote = "Old text that is not the working version"
        };
        var prompt = WorkflowTestData.Prompt(kind: "feedback", feedback: feedback, assignment: Assignment("producer"), workingArtifacts:
        [
            new { artifactId = "existing-artifact", title = "Existing draft", revision = 3, acceptedRevision = 3, spec }
        ]);

        var reply = await provider.CompleteAsync(Request("producer", prompt), _ => Task.CompletedTask, CancellationToken.None);
        var proposal = Assert.Single(JsonSerializer.Deserialize<ProducerResponse>(reply.Text, JsonDefaults.Options)!.Artifacts);
        SpecValidator.Validate(proposal.Spec, "artifact");
        Assert.Equal("existing-artifact", proposal.ArtifactId);
        Assert.Equal(spec.Root, proposal.Spec.Root);
        Assert.Equal(spec.Elements.Keys.Order(), proposal.Spec.Elements.Keys.Order());
        Assert.Contains("Latest human-edited objective", reply.Text);
        Assert.Contains("Use everyday language", reply.Text);
        Assert.DoesNotContain("Old text that is not the working version", reply.Text);
        Assert.Equal(original, WorkflowTestData.Json(spec));
    }

    [Fact]
    public async Task DemoCancellationStopsTheDelayWithoutReturningAProposal()
    {
        var provider = Provider(1000);
        using var stop = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = provider.CompleteAsync(Request("producer"), signal =>
        {
            if (signal.Kind == "session") started.TrySetResult();
            return Task.CompletedTask;
        }, stop.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        stop.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => completion.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task CrossWorkspaceSessionReuseFailsExplicitly()
    {
        var provider = Provider();
        var first = await provider.CompleteAsync(Request("planner"), _ => Task.CompletedTask, CancellationToken.None);
        var error = await Assert.ThrowsAsync<AgentProviderException>(() => provider.CompleteAsync(
            Request("planner", workspace: "workspace-two") with { SessionId = first.SessionId },
            _ => Task.CompletedTask, CancellationToken.None));
        Assert.Equal("session_mismatch", error.Code);
    }

    [Theory]
    [InlineData("not JSON")]
    [InlineData("{}")]
    [InlineData("{\"version\":1,\"unknown\":true}")]
    public async Task InvalidRequestsDoNotProduceSuccessShapedFallbacks(string prompt)
    {
        var signals = new List<AgentSignal>();
        var error = await Assert.ThrowsAsync<AgentProviderException>(() => Provider().CompleteAsync(
            Request("producer", prompt), signal =>
            {
                signals.Add(signal);
                return Task.CompletedTask;
            }, CancellationToken.None));
        Assert.Equal("invalid_request", error.Code);
        Assert.Empty(signals);
    }

    [Fact]
    public async Task FeedbackJobsWithoutTheirOriginalDirectionAreRejected()
    {
        var error = await Assert.ThrowsAsync<AgentProviderException>(() => Provider().CompleteAsync(
            Request("producer", WorkflowTestData.Prompt(kind: "feedback", assignment: Assignment("producer"))),
            _ => Task.CompletedTask, CancellationToken.None));
        Assert.Equal("invalid_request", error.Code);
    }

    private static DemoAgentProvider Provider(int delay = 0) => new(new WorkspaceOptions { DemoDelayMilliseconds = delay });

    private static string DefaultAnswer(UiElement question) => question.Type switch
    {
        "SliderQuestion" or "NumberQuestion" => question.Props["value"]!.GetValue<decimal>().ToString(CultureInfo.InvariantCulture),
        "MultiChoiceQuestion" => question.Props["value"]!.ToJsonString(),
        "ToggleQuestion" => question.Props["value"]!.GetValue<bool>() ? "true" : "false",
        _ => question.Props["value"]?.GetValue<string>() ?? ""
    };

    private static AgentAssignment Assignment(string kind, string? id = null) =>
        new(id ?? (kind == "producer" ? "writer" : "reviewer"),
            kind == "producer" ? "Demo author" : "Demo checker",
            kind == "producer" ? "Prepare the selected task draft." : "Check the actual draft.",
            kind == "producer" ? "produce" : "review");

    private static AgentRequest Request(
        string kind, string? prompt = null, string? agent = null, string workspace = "workspace-one") =>
        new(workspace, agent ?? (kind == "producer" ? "writer" : kind), null, "Trusted test instructions.",
            prompt ?? WorkflowTestData.Prompt(workspaceId: workspace,
                assignment: kind == "planner" ? null : Assignment(kind, agent),
                proposals: kind == "reviewer" ? [new(null, "Draft", ServiceTestData.Artifact())] : []), kind);
}
