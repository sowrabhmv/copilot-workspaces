using System.Text.Json;
using System.Text.Json.Nodes;
using Workspace.Server;

namespace Workspace.Server.Tests;

internal static class ServiceTestData
{
    public static JsonObject Props(object value) =>
        JsonSerializer.SerializeToNode(value, JsonDefaults.Options)!.AsObject();

    public static UiSpec Artifact(string text = "Original working draft") => new("document", new()
    {
        ["document"] = new("Section", Props(new { anchorId = "document", title = "Working draft", description = (string?)null }), ["heading", "body"]),
        ["heading"] = new("Heading", Props(new { anchorId = "heading", level = "h2", text = "The objective" }), []),
        ["body"] = new("Text", Props(new { anchorId = "body", text }), [])
    });

    public static UiSpec Questions() => new("questions", new()
    {
        ["questions"] = new("Section", Props(new { anchorId = "questions", title = "A few decisions", description = (string?)null }), ["audience", "constraints"]),
        ["audience"] = new("ChoiceQuestion", Props(new
        {
            anchorId = "audience", label = "Who is this for?", help = (string?)null, required = true,
            options = new[] { new { id = "team", label = "The team" }, new { id = "leaders", label = "Leadership" } }, value = (string?)null
        }), []),
        ["constraints"] = new("TextQuestion", Props(new
        {
            anchorId = "constraints", label = "Any constraints?", help = (string?)null,
            required = false, multiline = true, value = (string?)null
        }), [])
    });

    public static UiSpec VisualQuestions() => new("questions", new()
    {
        ["questions"] = new("Section", Props(new { anchorId = "questions", title = "Shape the next draft", description = (string?)null }),
            ["detail", "stakeholders", "budget", "review", "deadline", "notes"]),
        ["detail"] = new("SliderQuestion", Props(new
        {
            anchorId = "detail", label = "How detailed should the plan be?", help = (string?)null, required = true,
            min = 1, max = 5, step = 1, value = 3, minLabel = "Executive Brief", maxLabel = "Full Playbook",
            unit = (string?)null, labels = new[] { "Executive Brief", "Quick Summary", "Standard Plan", "Detailed Plan", "Full Playbook" }
        }), []),
        ["stakeholders"] = new("MultiChoiceQuestion", Props(new
        {
            anchorId = "stakeholders", label = "Who is this for?", help = (string?)null, required = true,
            options = new[] { new { id = "product", label = "Product" }, new { id = "marketing", label = "Marketing" }, new { id = "sales", label = "Sales" } },
            value = new[] { "product", "marketing" }
        }), []),
        ["budget"] = new("NumberQuestion", Props(new
        {
            anchorId = "budget", label = "What is the budget?", help = (string?)null, required = false,
            min = 0, max = 10000, step = 0.5, value = 0, unit = "USD"
        }), []),
        ["review"] = new("ToggleQuestion", Props(new
        {
            anchorId = "review", label = "Include an independent agent review", help = (string?)null, required = false, value = false
        }), []),
        ["deadline"] = new("DateQuestion", Props(new
        {
            anchorId = "deadline", label = "When is it needed?", help = (string?)null, required = false,
            min = "2026-09-20", max = "2026-12-31", value = (string?)null
        }), []),
        ["notes"] = new("TextQuestion", Props(new
        {
            anchorId = "notes", label = "Anything else to consider?", help = (string?)null,
            required = false, multiline = true, value = (string?)null
        }), [])
    });

    public static Dictionary<string, string> AnswersFor(UiSpec spec) =>
        spec.Elements.Where(pair => SpecValidator.IsQuestion(pair.Value.Type)).ToDictionary(
            pair => pair.Key, pair => pair.Value.Type switch
            {
                "ChoiceQuestion" => pair.Value.Props["value"]?.GetValue<string>()
                    ?? pair.Value.Props["options"]![0]!["id"]!.GetValue<string>(),
                "MultiChoiceQuestion" => pair.Value.Props["value"]?.ToJsonString()
                    ?? JsonSerializer.Serialize(new[] { pair.Value.Props["options"]![0]!["id"]!.GetValue<string>() }),
                "SliderQuestion" or "NumberQuestion" => pair.Value.Props["value"]?.ToJsonString()
                    ?? pair.Value.Props["min"]?.ToJsonString() ?? "0",
                "ToggleQuestion" => pair.Value.Props["value"]!.ToJsonString(),
                "DateQuestion" => pair.Value.Props["value"]?.GetValue<string>()
                    ?? pair.Value.Props["min"]?.GetValue<string>() ?? "2026-10-01",
                _ => "Keep the plan practical and explicit about assumptions."
            });

    public static WorkspaceDocument Seed(WorkspaceStore store)
    {
        var created = store.Create("Prepare a clear and accountable project plan", "demo");
        return store.Update(created.Id, "test.seeded", "Seeded a saved draft.", document =>
        {
            document.Jobs[0].Status = "completed";
            document.Jobs[0].CompletedAt = DateTimeOffset.UtcNow;
            document.Artifacts.Add(new()
            {
                Id = "artifact-one", Title = "Working draft", CurrentRevision = 1, AcceptedRevision = 1,
                Revisions = [new() { Revision = 1, Spec = Artifact(), Source = "human", Status = "accepted" }]
            });
        });
    }

    public static FeedbackRequest Feedback(string text = "Make this more concise", string? requestId = null) =>
        new(text, requestId ?? Guid.NewGuid().ToString(), "artifact-one", "body", 1);
}

internal sealed class ServiceFixture : IDisposable
{
    public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "workspaces-tests", Guid.NewGuid().ToString("N"));
    public WorkspaceOptions Options { get; }
    public WorkspaceStore Store { get; private set; }
    public WorkspaceService Service { get; private set; }

    public ServiceFixture()
    {
        Options = new() { DataDirectory = DirectoryPath, DemoDelayMilliseconds = 1, ProviderTimeoutSeconds = 10 };
        Store = new(Options);
        Service = new(Store);
    }

    public void Reopen()
    {
        Store.Dispose();
        Store = new(Options);
        Service = new(Store);
    }

    public void Dispose()
    {
        Store.Dispose();
        Directory.Delete(DirectoryPath, recursive: true);
    }
}
