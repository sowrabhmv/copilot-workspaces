using System.Text;
using System.Text.Json;

namespace Workspace.Server.Orchestration;

internal static class AgentOutputValidator
{
    public static ValidatedAgentPlan Planner(string text, AgentPromptContext input)
    {
        using var json = Parse(text);
        RequirePlanProperties(json.RootElement);
        var result = Deserialize<PlannerResponse>(json);
        Summary(result.Summary, 4000);
        var assignments = result.Agents ?? [];
        var artifacts = result.Artifacts ?? [];
        AgentPlanPolicy.ValidateAssignments(assignments);
        if (json.RootElement.TryGetProperty("agents", out var rawAgents) && rawAgents.ValueKind != JsonValueKind.Null)
        {
            foreach (var assignment in rawAgents.EnumerateArray())
                RequireProperties(assignment, "id", "name", "role", "kind");
        }

        if (result.Clarification is not null)
        {
            if (!input.ClarificationAllowed)
                throw Invalid("The coordinator requested blocking clarification after answers or refinement.");
            if (assignments.Count != 0 || artifacts.Count != 0)
                throw InvalidPlan("A clarification must not also recruit specialists or produce artifacts.");
            SpecEnvelope(json.RootElement.GetProperty("clarification"));
            SpecValidator.Validate(result.Clarification, "clarification");
            ClarificationQualityPolicy.ValidateNewForm(result.Clarification);
        }
        else
        {
            if (input.JobKind == "clarify")
                throw Invalid("An explicit question refresh must return a fresh clarification form.");
            if (assignments.Count == 0 && artifacts.Count == 0)
                throw InvalidPlan("The coordinator must produce a draft or recruit a useful task-specific team.");
            if (artifacts.Count != 0)
                ValidateArtifacts(artifacts, json.RootElement.GetProperty("artifacts"), input);
            var hasDraft = artifacts.Count != 0 || input.WorkingArtifacts.Count != 0;
            foreach (var assignment in assignments)
            {
                if (assignment.Kind == "review" && !hasDraft)
                    throw InvalidPlan("A review specialist needs a direct, existing, or earlier specialist draft.");
                if (assignment.Kind == "produce") hasDraft = true;
            }
        }
        var totalIdentities = input.ExistingAgents.Select(agent => agent.Id)
            .Union(assignments.Select(agent => agent.Id), StringComparer.Ordinal).Count();
        if (totalIdentities > WorkspaceOptions.MaximumSavedSpecialists)
            throw new WorkspaceException("agent_limit",
                "Reuse an appropriate saved specialist; this plan exceeds the workspace's saved-agent limit.");
        return new(result.Summary, result.Clarification, assignments, artifacts);
    }

    public static ProducerResponse Producer(string text, AgentPromptContext input)
    {
        using var json = Parse(text);
        RequireProperties(json.RootElement, "artifacts");
        var result = Deserialize<ProducerResponse>(json);
        ValidateArtifacts(result.Artifacts, json.RootElement.GetProperty("artifacts"), input);
        return result;
    }

    private static void ValidateArtifacts(List<DraftArtifact>? artifacts, JsonElement rawArtifacts, AgentPromptContext input)
    {
        if (artifacts is null || artifacts.Count is < 1 or > 3)
            throw Invalid("A candidate set must contain between one and three proposals.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var artifact in artifacts)
        {
            if (artifact is null || string.IsNullOrWhiteSpace(artifact.Title) || artifact.Title.Length > 180)
                throw Invalid("Every proposal needs a title of at most 180 characters.");
            RequireProperties(rawArtifacts[index], "artifactId", "title", "spec");
            SpecEnvelope(rawArtifacts[index].GetProperty("spec"));
            SpecValidator.Validate(artifact.Spec, "artifact");
            if (artifact.ArtifactId is not null)
            {
                if (!seen.Add(artifact.ArtifactId))
                    throw Invalid("The producer returned the same artifact more than once.");
                var current = input.WorkingArtifacts.SingleOrDefault(item => item.ArtifactId == artifact.ArtifactId)
                    ?? throw Invalid("A proposal refers to an artifact outside the supplied working context.");
                PreserveIds(current.Spec, artifact.Spec);
                if (input.Proposals?.SingleOrDefault(candidate => candidate.ArtifactId == artifact.ArtifactId) is { } previous)
                    PreserveIds(previous.Spec, artifact.Spec);
            }
            else if (input.Proposals is { } candidates && index < candidates.Count && candidates[index].ArtifactId is null)
                PreserveIds(candidates[index].Spec, artifact.Spec);
            index++;
        }

        if (input.Feedback?.ArtifactId is { } target &&
            (artifacts.Count != 1 || artifacts[0].ArtifactId != target))
            throw Invalid("Targeted feedback must return exactly its existing artifact, not a new artifact.");
        if (input.Proposals is { Count: > 0 } previousCandidates && artifacts.Count < previousCandidates.Count)
            throw Invalid("A later producer must return the complete candidate set, not a partial update.");
        if (input.Proposals is { } upstream &&
            upstream.Any(previous => previous.ArtifactId is not null && !seen.Contains(previous.ArtifactId)))
            throw Invalid("A later producer cannot omit an existing upstream candidate.");
    }

    public static ReviewerResponse Reviewer(string text, int maximumCharacters)
    {
        using var json = Parse(text);
        RequireProperties(json.RootElement, "summary");
        var result = Deserialize<ReviewerResponse>(json);
        Summary(result.Summary, maximumCharacters);
        return result;
    }

    private static void PreserveIds(UiSpec previous, UiSpec next)
    {
        if (previous.Root != next.Root || previous.Elements.Keys.Any(id => !next.Elements.ContainsKey(id)))
            throw Invalid("A refinement must preserve the existing root and element identifiers.");
    }

    private static void RequirePlanProperties(JsonElement value)
    {
        var names = value.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
        if (!names.Contains("summary") || !names.Contains("clarification") ||
            names.Except(["summary", "clarification", "agents", "artifacts"], StringComparer.Ordinal).Any())
            throw Invalid("The coordinator response contains missing, unknown, or incorrectly cased fields.");
    }

    private static JsonDocument Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || Encoding.UTF8.GetByteCount(text) > 1024 * 1024)
            throw Invalid("The agent returned empty or oversized structured output.");
        JsonDocument json;
        try
        {
            json = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = JsonDefaults.Options.MaxDepth });
        }
        catch (JsonException exception)
        {
            throw new AgentProviderException("invalid_output",
                "The agent response must be one valid JSON object, without Markdown fences.", exception);
        }
        try
        {
            if (json.RootElement.ValueKind != JsonValueKind.Object)
                throw Invalid("The agent response must be a JSON object.");
            UniqueProperties(json.RootElement);
            return json;
        }
        catch (AgentProviderException)
        {
            json.Dispose();
            throw;
        }
    }

    private static T Deserialize<T>(JsonDocument json)
    {
        try
        {
            return json.RootElement.Deserialize<T>(JsonDefaults.Options)
                ?? throw Invalid("The agent response is null.");
        }
        catch (JsonException exception)
        {
            throw new AgentProviderException("invalid_output",
                "The agent response does not match its role's output contract.", exception);
        }
    }

    private static void Summary(string? value, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum)
            throw Invalid($"An agent summary must contain between one and {maximum} characters.");
    }

    private static void SpecEnvelope(JsonElement spec)
    {
        RequireProperties(spec, "root", "elements");
        var elements = spec.GetProperty("elements");
        if (elements.ValueKind != JsonValueKind.Object)
            throw Invalid("A document's elements must be an object.");
        foreach (var element in elements.EnumerateObject())
            RequireProperties(element.Value, "type", "props", "children");
    }

    private static void RequireProperties(JsonElement value, params string[] required)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw Invalid("The agent response contains an invalid object.");
        var names = value.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
        if (!names.SetEquals(required))
            throw Invalid("The agent response contains missing, unknown, or incorrectly cased fields.");
    }

    private static void UniqueProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw Invalid("The agent response contains duplicate JSON properties.");
                UniqueProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in value.EnumerateArray())
                UniqueProperties(child);
        }
    }

    private static AgentProviderException Invalid(string message) => new("invalid_output", message);
    private static WorkspaceException InvalidPlan(string message) => new("invalid_agent_plan", message);
}

internal sealed record ValidatedAgentPlan(
    string Summary, UiSpec? Clarification, List<AgentAssignment> Agents, List<DraftArtifact> Artifacts);
