using System.Text.Json;
using System.Text.Json.Nodes;
using Workspace.Server;

namespace Workspace.Server.Tests;

public sealed class SpecValidatorTests
{
    [Fact]
    public void AllowsOnlyTheCorrectDocumentKinds()
    {
        SpecValidator.Validate(ServiceTestData.Artifact(), "artifact");
        SpecValidator.Validate(ServiceTestData.Questions(), "clarification");
        Assert.Throws<WorkspaceException>(() => SpecValidator.Validate(ServiceTestData.Questions(), "artifact"));
        Assert.Throws<WorkspaceException>(() => SpecValidator.Validate(ServiceTestData.Artifact(), "clarification"));
    }

    [Theory]
    [InlineData("on")]
    [InlineData("watch")]
    [InlineData("state")]
    [InlineData("visible")]
    [InlineData("repeat")]
    public void WireDeserializerRejectsModelActionsAndExpressions(string field)
    {
        var json = JsonSerializer.SerializeToNode(ServiceTestData.Artifact(), JsonDefaults.Options)!.AsObject();
        json["elements"]!["body"]![field] = new JsonObject { ["action"] = "push" };
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<UiSpec>(json.ToJsonString(), JsonDefaults.Options));
    }

    [Fact]
    public void RejectsUnknownPropsDynamicTextAndWrongAnchors()
    {
        var extra = ServiceTestData.Artifact();
        extra.Elements["body"].Props["href"] = "https://untrusted.example/";
        Assert.Throws<WorkspaceException>(() => SpecValidator.Validate(extra, "artifact"));
        var dynamic = ServiceTestData.Artifact();
        dynamic.Elements["body"].Props["text"] = new JsonObject { ["$state"] = "/secret" };
        Assert.Throws<WorkspaceException>(() => SpecValidator.Validate(dynamic, "artifact"));
        var anchor = ServiceTestData.Artifact();
        anchor.Elements["body"].Props["anchorId"] = "different";
        Assert.Throws<WorkspaceException>(() => SpecValidator.Validate(anchor, "artifact"));
    }

    [Fact]
    public void RejectsOrphansSharedChildrenCyclesAndUnknownComponents()
    {
        var orphan = ServiceTestData.Artifact();
        orphan.Elements["unused"] = new("Text", ServiceTestData.Props(new { anchorId = "unused", text = "Not connected" }), []);
        Assert.Throws<WorkspaceException>(() => SpecValidator.Validate(orphan, "artifact"));
        var shared = ServiceTestData.Artifact();
        shared.Elements["document"].Children.Add("body");
        Assert.Throws<WorkspaceException>(() => SpecValidator.Validate(shared, "artifact"));
        var cycle = ServiceTestData.Artifact();
        cycle.Elements["document"].Children.Add("document");
        Assert.Throws<WorkspaceException>(() => SpecValidator.Validate(cycle, "artifact"));
        var unknown = ServiceTestData.Artifact();
        unknown.Elements["body"] = unknown.Elements["body"] with { Type = "RawHtml" };
        Assert.Throws<WorkspaceException>(() => SpecValidator.Validate(unknown, "artifact"));
    }

    [Fact]
    public void EnforcesExactNodeAndDepthLimits()
    {
        var elements = new Dictionary<string, UiElement>
        {
            ["root"] = Section("root", ["group0", "group1", "group2", "group3"])
        };
        for (var group = 0; group < 4; group++)
        {
            var children = new List<string>();
            for (var index = 0; index < (group == 3 ? 22 : 23); index++)
            {
                var id = $"text{group}_{index}";
                children.Add(id);
                elements[id] = new("Text", ServiceTestData.Props(new { anchorId = id, text = "A bounded block" }), []);
            }
            elements[$"group{group}"] = Section($"group{group}", children);
        }
        Assert.Equal(96, elements.Count);
        SpecValidator.Validate(new("root", elements), "artifact");
        elements["extra"] = new("Text", ServiceTestData.Props(new { anchorId = "extra", text = "One too many" }), []);
        elements["group3"].Children.Add("extra");
        Assert.Throws<WorkspaceException>(() => SpecValidator.Validate(new("root", elements), "artifact"));

        var deep = new Dictionary<string, UiElement>();
        for (var index = 0; index < 9; index++)
            deep[$"level{index}"] = Section($"level{index}", index < 8 ? [$"level{index + 1}"] : []);
        Assert.Throws<WorkspaceException>(() => SpecValidator.Validate(new("level0", deep), "artifact"));
    }

    [Fact]
    public void ValidatesAnswersAgainstRequiredFieldsAndOfferedOptions()
    {
        var questions = ServiceTestData.Questions();
        Assert.Throws<WorkspaceException>(() => SpecValidator.ValidateAnswers(questions, []));
        Assert.Throws<WorkspaceException>(() => SpecValidator.ValidateAnswers(questions, new() { ["audience"] = "unknown" }));
        Assert.Throws<WorkspaceException>(() => SpecValidator.ValidateAnswers(questions, new() { ["audience"] = "team", ["foreign"] = "x" }));
        var answers = SpecValidator.ValidateAnswers(questions, new() { ["audience"] = "team", ["constraints"] = " Be concise " });
        Assert.Equal("Be concise", answers["constraints"]);
    }

    [Fact]
    public void ValidatesTableWidthsAndDecisionReferences()
    {
        var spec = ServiceTestData.Artifact();
        spec.Elements["document"].Children.Add("table");
        spec.Elements["table"] = new("DataTable", ServiceTestData.Props(new
        {
            anchorId = "table", caption = (string?)null,
            columns = new[] { new { id = "owner", label = "Owner" } },
            rows = new[] { new { id = "first", cells = new[] { "You" } } }
        }), []);
        SpecValidator.Validate(spec, "artifact");
        spec.Elements["table"].Props["rows"]![0]!["cells"]!.AsArray().Add("Extra");
        Assert.Throws<WorkspaceException>(() => SpecValidator.Validate(spec, "artifact"));
    }

    private static UiElement Section(string id, List<string> children) =>
        new("Section", ServiceTestData.Props(new { anchorId = id, title = "Section", description = (string?)null }), children);
}
