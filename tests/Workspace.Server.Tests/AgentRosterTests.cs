using System.Text.Json;
using Workspace.Server;

namespace Workspace.Server.Tests;

public sealed class AgentRosterTests
{
    [Fact]
    public void NewTasksHaveOnlyACoordinatorAndNoForcedSpecialistSlots()
    {
        using var fixture = new ServiceFixture();
        var workspace = fixture.Service.Create(new("Write one clear sentence", "demo"));
        var coordinator = Assert.Single(workspace.Agents);
        Assert.Equal("planner", coordinator.Id);
        Assert.Equal("Coordinator", coordinator.Name);
        Assert.True(coordinator.Assigned);
        Assert.DoesNotContain("agent-writer", workspace.Layout.Positions.Keys);
        Assert.DoesNotContain("agent-reviewer", workspace.Layout.Positions.Keys);
    }

    [Fact]
    public void PlansChooseDifferentTeamsAndPreserveUnusedSessionHistory()
    {
        using var fixture = new ServiceFixture();
        var workspace = ServiceTestData.Seed(fixture.Store);
        AgentPlanPolicy.ApplyRoster(workspace,
        [
            new("analyst", "Options analyst", "Compare the user's alternatives.", "produce"),
            new("risk-check", "Risk checker", "Check assumptions only where material.", "review")
        ]);
        Assert.Equal(["planner", "analyst", "risk-check"], workspace.Agents.Where(a => a.Assigned).Select(a => a.Id));
        workspace.Agents.Single(a => a.Id == "analyst").SessionId = "saved-specialist-session";
        workspace.Layout.Positions["agent-analyst"] = new(900, 440);

        AgentPlanPolicy.ApplyRoster(workspace, []);
        Assert.Single(workspace.Agents, agent => agent.Assigned);
        Assert.Equal("saved-specialist-session", workspace.Agents.Single(a => a.Id == "analyst").SessionId);
        AgentPlanPolicy.ApplyRoster(workspace, [new("analyst", "Options analyst", "Refine the comparison.", "produce")]);
        Assert.Equal(2, workspace.Agents.Count(a => a.Assigned));
        Assert.Equal(new(900, 440), workspace.Layout.Positions["agent-analyst"]);
        Assert.Equal("saved-specialist-session", workspace.Agents.Single(a => a.Id == "analyst").SessionId);
    }

    [Theory]
    [InlineData("planner", "produce")]
    [InlineData("Planner", "produce")]
    [InlineData("unsafe/id", "produce")]
    [InlineData("constructor", "produce")]
    [InlineData("worker", "execute")]
    public void InvalidDelegationCannotExpandTheApplicationCapabilities(string id, string kind) =>
        Assert.Throws<WorkspaceException>(() => AgentPlanPolicy.ValidateAssignments(
            [new(id, "Worker", "A bounded task purpose.", kind)]));

    [Fact]
    public void SpecialistAndSavedIdentityBudgetsAreEnforced()
    {
        Assert.Throws<WorkspaceException>(() => AgentPlanPolicy.ValidateAssignments(
            Enumerable.Range(1, 5).Select(i => new AgentAssignment($"agent-{i}", $"Agent {i}", "A task purpose.", "produce")).ToArray()));
        Assert.Throws<WorkspaceException>(() => AgentPlanPolicy.ValidateAssignments(
            [new("same", "One", "Task one.", "produce"), new("Same", "Two", "Task two.", "review")]));
        using var fixture = new ServiceFixture();
        var workspace = ServiceTestData.Seed(fixture.Store);
        for (var index = 0; index < 12; index++)
            workspace.Agents.Add(new() { Id = $"saved-{index}", Name = "Saved", Role = "Historical role", Assigned = false });
        Assert.Throws<WorkspaceException>(() => AgentPlanPolicy.ApplyRoster(workspace,
            [new("extra", "Extra", "Exceeds the saved identity budget.", "produce")]));
    }

    [Fact]
    public void LegacyAgentRecordsRemainReadableAndGeneratedPlansCannotChooseToolsOrModels()
    {
        var legacy = JsonSerializer.Deserialize<AgentState>(
            """{"id":"planner","name":"Planner","role":"Existing saved role"}""", JsonDefaults.Options);
        Assert.True(legacy!.Assigned);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<AgentAssignment>(
            """{"id":"worker","name":"Worker","role":"A task purpose","kind":"produce","model":"manual"}""", JsonDefaults.Options));
    }

    [Theory]
    [InlineData("""{"id":"writer","name":"Producer","role":"Original placeholder","status":"waiting"}""", false)]
    [InlineData("""{"id":"reviewer","name":"Reviewer","role":"Original placeholder","status":"idle"}""", false)]
    [InlineData("""{"id":"writer","name":"Producer","role":"Previously used","status":"complete","sessionId":"saved-history"}""", true)]
    [InlineData("""{"id":"writer","name":"Producer","role":"Newly recruited","status":"idle","assigned":true}""", true)]
    [InlineData("""{"id":"writer","name":"Producer","role":"Inactive history","status":"complete","sessionId":"saved-history","assigned":false}""", false)]
    public void LegacyUnusedPlaceholdersAreHiddenWithoutRemovingHistoryOrNewRecruitments(string json, bool expected)
    {
        var agent = JsonSerializer.Deserialize<AgentState>(json, JsonDefaults.Options)!;
        Assert.Equal(expected, agent.Assigned);
        Assert.Equal(expected, JsonDefaults.Clone(agent).Assigned);
    }
}
