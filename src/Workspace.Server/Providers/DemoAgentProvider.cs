using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Workspace.Server.Orchestration;

namespace Workspace.Server.Providers;

public sealed class DemoAgentProvider : IAgentProvider
{
    private static readonly string[] DetailLabels =
        ["Executive Brief", "Concise Overview", "Working Plan", "Detailed Guide", "Full Playbook"];
    private static readonly Dictionary<string, string> Stakeholders = new(StringComparer.Ordinal)
    {
        ["product"] = "Product", ["marketing"] = "Marketing", ["engineering"] = "Engineering",
        ["sales"] = "Sales", ["support"] = "Support", ["leadership"] = "Leadership"
    };
    private readonly int _delayMilliseconds;
    public string Name => "demo";

    public DemoAgentProvider(WorkspaceOptions options)
    {
        if (options.DemoDelayMilliseconds is < 0 or > 5000)
            throw new ArgumentOutOfRangeException(nameof(options.DemoDelayMilliseconds),
                "The deterministic demo delay must be between 0 and 5000 milliseconds.");
        _delayMilliseconds = options.DemoDelayMilliseconds;
    }

    public Task<ProviderStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new ProviderStatus(
            Name, "ready", "Deterministic demo. No model calls or external actions are performed.", "demo", null));
    }

    public async Task<AgentReply> CompleteAsync(
        AgentRequest request, Func<AgentSignal, Task> onSignal, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var input = ReadContext(request);
        var sessionId = SessionId(request.WorkspaceId, request.AgentId);
        if (request.SessionId is not null && request.SessionId != sessionId)
            throw new AgentProviderException("session_mismatch",
                "This demo session belongs to a different workspace or agent.");

        await onSignal(new(request.AgentId, "session", "Deterministic demo session ready.", sessionId));
        cancellationToken.ThrowIfCancellationRequested();
        await onSignal(new(request.AgentId, "status", "Running the deterministic demo; no model is being called."));
        await Task.Delay(_delayMilliseconds, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        object output = request.OutputKind switch
        {
            "planner" => Plan(input),
            "producer" => Produce(input),
            "reviewer" => Review(input),
            _ => throw Invalid("The demo does not support this output kind.")
        };
        cancellationToken.ThrowIfCancellationRequested();
        return new(JsonSerializer.Serialize(output, JsonDefaults.Options), sessionId);
    }

    private static AgentPromptContext ReadContext(AgentRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.WorkspaceId) || !SpecValidator.IsId(request.AgentId) ||
            request.OutputKind is not ("planner" or "producer" or "reviewer") ||
            string.IsNullOrWhiteSpace(request.Prompt) || request.Prompt.Length > 2 * 1024 * 1024)
            throw Invalid("The demo received an invalid agent request.");
        AgentPromptContext input;
        try
        {
            input = JsonSerializer.Deserialize<AgentPromptContext>(request.Prompt, JsonDefaults.Options)
                ?? throw Invalid("The demo context is missing.");
        }
        catch (JsonException exception)
        {
            throw new AgentProviderException("invalid_request", "The demo requires a valid workflow context.", exception);
        }
        if (input.Version != 2 || input.WorkspaceId != request.WorkspaceId ||
            string.IsNullOrWhiteSpace(input.Objective) || input.Answers is null || input.WorkingArtifacts is null ||
            input.ExistingAgents is null || input.JobKind is not ("initial" or "answers" or "feedback" or "clarify") ||
            ((input.JobKind == "feedback") != (input.Feedback is not null)) ||
            (input.ClarificationAllowed &&
             input.JobKind != "clarify" &&
             (input.JobKind != "initial" || input.Answers.Count != 0 || input.WorkingArtifacts.Count != 0)) ||
            (input.JobKind == "clarify" && (input.Clarification is null || !input.ClarificationAllowed)))
            throw Invalid("The demo context does not match this workspace or workflow stage.");
        if (input.Feedback is { } feedback &&
            (string.IsNullOrWhiteSpace(feedback.Text) || string.IsNullOrWhiteSpace(feedback.TargetLabel) ||
             (feedback.ArtifactId is not null && feedback.Revision is null) ||
             (feedback.ArtifactId is null && (feedback.ElementId is not null || feedback.Revision is not null))))
            throw Invalid("The demo feedback context is incomplete.");
        foreach (var artifact in input.WorkingArtifacts)
        {
            if (artifact is null)
                throw Invalid("The demo working context contains an empty artifact.");
            SpecValidator.Validate(artifact.Spec, "artifact");
        }
        if (input.Proposals is { } candidates)
        {
            if (candidates.Count > 3) throw Invalid("The demo candidate set is too large.");
            foreach (var candidate in candidates)
            {
                if (candidate is null) throw Invalid("The demo candidate set contains an empty artifact.");
                SpecValidator.Validate(candidate.Spec, "artifact");
            }
        }
        if ((request.OutputKind is "producer" or "reviewer") && string.IsNullOrWhiteSpace(input.PlannerSummary))
            throw Invalid("The demo stage is missing the actual planner output.");
        if (request.OutputKind == "planner")
        {
            if (request.AgentId != AgentPlanPolicy.CoordinatorId || input.Assignment is not null)
                throw Invalid("Only the coordinator can plan the team.");
        }
        else
        {
            if (input.Assignment is null || input.Assignment.Id != request.AgentId ||
                input.Assignment.Kind != (request.OutputKind == "producer" ? "produce" : "review"))
                throw Invalid("The demo specialist must match its declared assignment.");
            AgentPlanPolicy.ValidateAssignments([input.Assignment]);
        }
        if (input.ReviewCharacterLimit is < 100 or > 8000)
            throw Invalid("The demo review budget is invalid.");
        _ = ReadDecisions(input);
        return input;
    }

    private static PlannerResponse Plan(AgentPromptContext input)
    {
        var decisions = ReadDecisions(input);
        if (input.JobKind != "clarify" && input.Feedback is null && input.WorkingArtifacts.Count == 0 &&
            IsSimpleCompleteTask(input.Objective))
        {
            var directProfile = Profile(input.Objective);
            List<AgentAssignment> reviewers = decisions.Review
                ? [Reuse(input, directProfile.ReviewerId, directProfile.ReviewerName, directProfile.ReviewPurpose, "review")]
                : [];
            return new(decisions.Review
                    ? "The coordinator can draft directly; only the requested independent review needs a specialist."
                    : "The request is already specific enough for a short draft; no specialists are needed.",
                null, reviewers, [DirectDraft(input)]);
        }

        if (input.ClarificationAllowed)
        {
            var questions = Questions(input, decisions);
            if (questions is not null)
                return new("A few visual choices will make the next draft more useful.", questions, [], []);
        }

        var profile = Profile(input.Objective);
        var team = new List<AgentAssignment>();
        if (input.Feedback is not null && input.WorkingArtifacts.Count != 0 &&
            Has(input.Feedback.Text, "review only", "only review", "check risks"))
        {
            team.Add(Reuse(input, profile.ReviewerId, profile.ReviewerName, profile.ReviewPurpose, "review"));
        }
        else
        {
            team.Add(Reuse(input, profile.ProducerId, profile.ProducerName, profile.ProducePurpose, "produce"));
            if (decisions.Detail >= 4 && (decisions.StakeholderIds.Length >= 2 || profile.Family == "research"))
                team.Add(Reuse(input, profile.EditorId, profile.EditorName, profile.EditPurpose, "produce"));
            if (decisions.Review)
                team.Add(Reuse(input, profile.ReviewerId, profile.ReviewerName, profile.ReviewPurpose, "review"));
        }
        AgentPlanPolicy.ValidateAssignments(team);
        return new($"Use the saved decisions for '{Compact(input.Objective, 200)}'. " +
            $"The {DetailLabels[decisions.Detail - 1]} needs {team.Count} task-specific specialist(s); " +
            (team.Any(agent => agent.Kind == "review") ? "their review stays advisory." : "no AI reviewer is requested."),
            null, team, []);
    }

    private static UiSpec? Questions(AgentPromptContext input, DemoDecisions decisions)
    {
        var profile = Profile(input.Objective);
        var spec = new UiSpec("questions", new()
        {
            ["questions"] = new("Section", Props(new
            {
                anchorId = "questions", title = "Shape the next draft",
                description = "Confirm the suggestions or adjust what matters."
            }), [])
        });
        void Add(string id, string type, object props)
        {
            if (spec.Elements["questions"].Children.Count >= 4) return;
            spec.Elements.Add(id, new(type, Props(props), []));
            spec.Elements["questions"].Children.Add(id);
        }

        if (!HasSpecifiedDetail(input.Objective) && !input.Answers.ContainsKey("detail"))
            Add("detail", "SliderQuestion", new
            {
                anchorId = "detail", label = "How much detail?", help = (string?)null, required = true,
                min = 1, max = 5, step = 1, value = decisions.Detail,
                minLabel = "Executive Brief", maxLabel = "Full Playbook", unit = (string?)null, labels = DetailLabels
            });
        if (profile.Family == "communications")
        {
            if (!HasKnownAudience(input.Objective) && !input.Answers.ContainsKey("audience"))
                Add("audience", "ChoiceQuestion", new
                {
                    anchorId = "audience", label = "Who is this for?", help = (string?)null, required = true,
                    options = new[]
                    {
                        new { id = "team", label = "The team" },
                        new { id = "leadership", label = "Leadership" },
                        new { id = "customers", label = "Customers" }
                    },
                    value = "team"
                });
        }
        else if (DeclaredStakeholders(input.Objective).Length == 0 && !input.Answers.ContainsKey("stakeholders"))
            Add("stakeholders", "MultiChoiceQuestion", new
            {
                anchorId = "stakeholders", label = "Who needs to be involved?",
                help = "Choose the teams the draft should account for.", required = true,
                options = Stakeholders.Select(item => new { id = item.Key, label = item.Value }).ToArray(),
                value = new[] { "product", "marketing" }
            });

        if (Has(input.Objective, "deadline", "schedule", "event", "workshop", "offsite") &&
            !HasDate(input.Objective) && !input.Answers.ContainsKey("deadline"))
            Add("deadline", "DateQuestion", new
            {
                anchorId = "deadline", label = "When should this be ready?",
                help = "Choose a date rather than describing it.", required = true,
                min = (string?)null, max = (string?)null, value = (string?)null
            });
        if (Has(input.Objective, "budget", "cost") && !HasBudget(input.Objective) && !input.Answers.ContainsKey("budget"))
            Add("budget", "NumberQuestion", new
            {
                anchorId = "budget", label = "What is the planning budget?",
                help = "A suggested starting amount; confirm or change it.", required = true,
                min = (decimal?)0, max = (decimal?)1_000_000, step = 100m, value = (decimal?)1000, unit = (string?)null
            });
        else if (Has(input.Objective, "attendees", "participants", "guests", "volunteers") &&
                 !HasQuantity(input.Objective) && !input.Answers.ContainsKey("quantity"))
            Add("quantity", "NumberQuestion", new
            {
                anchorId = "quantity", label = "How many people should we plan for?",
                help = "This changes the scale of the proposal.", required = true,
                min = (decimal?)1, max = (decimal?)100_000, step = 1m, value = (decimal?)25, unit = "people"
            });
        if (!input.Answers.ContainsKey("review") && !Has(input.Objective, "no review", "without review", "review", "audit"))
            Add("review", "ToggleQuestion", new
            {
                anchorId = "review", label = "Add a specialist review?",
                help = "Optional. Human acceptance is still required either way.", required = false, value = profile.Family != "communications"
            });
        if (Has(input.Objective, "special requirements", "unknown constraints") && !input.Answers.ContainsKey("constraints"))
            Add("constraints", "TextQuestion", new
            {
                anchorId = "constraints", label = "What should we know that the choices do not cover?",
                help = (string?)null, required = false, multiline = true, value = (string?)null
            });
        if (spec.Elements.Count == 1)
        {
            if (input.JobKind != "clarify") return null;
            Add("emphasis", "ChoiceQuestion", new
            {
                anchorId = "emphasis", label = "What should the next draft emphasize?",
                help = (string?)null, required = true,
                options = new[]
                {
                    new { id = "clarity", label = "Clarity" },
                    new { id = "coverage", label = "Coverage" },
                    new { id = "actions", label = "Next actions" }
                },
                value = "clarity"
            });
        }
        SpecValidator.Validate(spec, "clarification");
        ClarificationQualityPolicy.ValidateNewForm(spec);
        return spec;
    }

    private static ProducerResponse Produce(AgentPromptContext input)
    {
        var assignment = input.Assignment ?? throw Invalid("A producer needs its declared purpose.");
        var upstream = input.Proposals is { Count: > 0 };
        var artifacts = input.Proposals is { Count: > 0 } previous ? JsonDefaults.Clone(previous) :
            input.WorkingArtifacts.Select(item => new DraftArtifact(item.ArtifactId, item.Title, JsonDefaults.Clone(item.Spec))).ToList();
        if (artifacts.Count > 3)
            throw Invalid("The demo needs at most three working candidates; target an individual artifact.");
        if (artifacts.Count == 0)
            artifacts.Add(new(null, $"Working plan: {Compact(input.Objective, 140)}", CreatePlan(input)));

        foreach (var artifact in artifacts)
        {
            if (!upstream && input.Feedback is not null)
                ApplyDirection(artifact.Spec, input.Feedback.ElementId, input.Feedback.Text);
            AddContribution(artifact.Spec, assignment, ReadDecisions(input));
            SpecValidator.Validate(artifact.Spec, "artifact");
        }
        return new(artifacts);
    }

    private static DraftArtifact DirectDraft(AgentPromptContext input)
    {
        var spec = new UiSpec("message", new()
        {
            ["message"] = new("Section", Props(new { anchorId = "message", title = "A short thank-you draft", description = (string?)null }), ["note"]),
            ["note"] = new("Text", Props(new
            {
                anchorId = "note",
                text = Has(input.Objective, "team")
                    ? "Thank you, team, for your thoughtful work and reliable follow-through."
                    : "Thank you for your thoughtful work and reliable follow-through."
            }), [])
        });
        SpecValidator.Validate(spec, "artifact");
        return new(null, "Thank-you draft", spec);
    }

    private static void AddContribution(UiSpec spec, AgentAssignment assignment, DemoDecisions decisions)
    {
        var id = "specialist-" + Digest(assignment.Id)[..16];
        if (!spec.Elements.ContainsKey(id))
        {
            if (spec.Elements.Count >= 96 || spec.Elements[spec.Root].Children.Count >= 24)
                throw new AgentProviderException("demo_output_limit", "This draft has no room for another specialist contribution.");
            spec.Elements[spec.Root].Children.Add(id);
        }
        spec.Elements[id] = new("Text", Props(new
        {
            anchorId = id,
            text = $"{assignment.Name} focus: {assignment.Role} " +
                $"Level: {DetailLabels[decisions.Detail - 1]}." +
                (decisions.StakeholderIds.Length == 0 ? "" :
                    $" Stakeholders: {string.Join(", ", decisions.StakeholderIds.Select(key => Stakeholders[key]))}.")
        }), []);
    }

    private static UiSpec CreatePlan(AgentPromptContext input)
    {
        var spec = new UiSpec("document", new()
        {
            ["document"] = new("Section", Props(new
            {
                anchorId = "document", title = "A reviewable working proposal",
                description = "Deterministic demo content, not model-generated work or a completed action."
            }), ["task-heading", "objective", "assumptions", "next-steps", "delivery-checklist", "approach", "answers"]),
            ["task-heading"] = new("Heading", Props(new { anchorId = "task-heading", level = "h2", text = "The outcome" }), []),
            ["objective"] = new("Text", Props(new { anchorId = "objective", text = $"Objective: {Compact(input.Objective, 7500)}" }), []),
            ["assumptions"] = new("Text", Props(new
            {
                anchorId = "assumptions",
                text = "This is a starting proposal. Dates, owners, sources, and performance claims have not been independently verified."
            }), []),
            ["next-steps"] = new("BulletList", Props(new
            {
                anchorId = "next-steps", ordered = true,
                items = new[]
                {
                    new { id = "define", text = $"Confirm what success means for: {Compact(input.Objective, 350)}" },
                    new { id = "draft", text = "Use the saved decisions to prepare a small, useful first version." },
                    new { id = "review", text = "Review the proposal, request changes where needed, and explicitly accept or reject it." }
                }
            }), []),
            ["delivery-checklist"] = new("DataTable", Props(new
            {
                anchorId = "delivery-checklist", caption = "Suggested responsibilities, not completed work",
                columns = new[]
                {
                    new { id = "step", label = "Step" },
                    new { id = "responsibility", label = "Responsibility" },
                    new { id = "result", label = "Reviewable result" }
                },
                rows = new[]
                {
                    new { id = "context", cells = new[] { "Confirm context", "You", "Outcome and constraints" } },
                    new { id = "proposal", cells = new[] { "Prepare a draft", "Agent workflow", "A proposed artifact" } },
                    new { id = "decision", cells = new[] { "Review", "Human reviewer", "An explicit accept or reject decision" } }
                }
            }), []),
            ["approach"] = new("Decision", Props(new
            {
                anchorId = "approach", question = "How broad should the first pass be?",
                options = new[]
                {
                    new { id = "small-pass", label = "Small first pass", reason = "Keeps assumptions visible and makes the first review manageable." },
                    new { id = "full-outline", label = "Full outline", reason = "Shows the broader structure, but requires more decisions before detailed execution." }
                },
                recommendedId = "small-pass"
            }), []),
            ["answers"] = new("Section", Props(new
            {
                anchorId = "answers", title = "Saved task decisions", description = (string?)null
            }), [])
        });
        if (input.Answers.Count == 0)
        {
            spec.Elements.Add("answers-empty", new("Text",
                Props(new { anchorId = "answers-empty", text = "No additional answers have been saved for this task." }), []));
            spec.Elements["answers"].Children.Add("answers-empty");
        }
        else
        {
            foreach (var (key, value) in input.Answers.OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                var id = "answer-" + Digest(key)[..16];
                spec.Elements.Add(id, new("Text",
                    Props(new { anchorId = id, text = AnswerText(key, value, ReadDecisions(input)) }), []));
                spec.Elements["answers"].Children.Add(id);
            }
        }
        return spec;
    }

    private static void ApplyDirection(UiSpec spec, string? elementId, string direction)
    {
        var target = elementId ?? spec.Root;
        if (!spec.Elements.TryGetValue(target, out var element))
            throw new AgentProviderException("feedback_target_changed",
                "The selected block is no longer present in the latest working revision.");
        if (element.Type == "Section")
        {
            var descendants = Descendants(spec, target).ToList();
            target = descendants.FirstOrDefault(id => spec.Elements[id].Type == "Text")
                ?? descendants.FirstOrDefault(id => spec.Elements[id].Type != "Section")
                ?? AddFeedbackText(spec, target);
            element = spec.Elements[target];
        }

        switch (element.Type)
        {
            case "Text":
            case "Heading":
                var text = element.Props["text"]!.GetValue<string>();
                if (element.Type == "Text" &&
                    (direction.Contains("concise", StringComparison.OrdinalIgnoreCase) ||
                     direction.Contains("shorter", StringComparison.OrdinalIgnoreCase)))
                    text = Compact(text, 180);
                element.Props["text"] = AppendDirection(text, direction, element.Type == "Text" ? 8000 : 180);
                break;
            case "BulletList":
                var item = element.Props["items"]!.AsArray()[0]!.AsObject();
                item["text"] = AppendDirection(item["text"]!.GetValue<string>(), direction, 2000);
                break;
            case "DataTable":
                var rows = element.Props["rows"]!.AsArray();
                var count = element.Props["columns"]!.AsArray().Count;
                if (rows.Count == 0)
                    rows.Add(Props(new { id = "demo-direction", cells = Enumerable.Repeat("", count).ToArray() }));
                var cells = rows[0]!["cells"]!.AsArray();
                cells[count - 1] = AppendDirection(cells[count - 1]!.GetValue<string>(), direction, 2000);
                break;
            case "Decision":
                var option = element.Props["options"]!.AsArray()[0]!.AsObject();
                option["reason"] = AppendDirection(option["reason"]!.GetValue<string>(), direction, 2000);
                break;
            default:
                throw Invalid("The demo cannot refine this component.");
        }
    }

    private static string AddFeedbackText(UiSpec spec, string sectionId)
    {
        if (spec.Elements.Count >= 96 || spec.Elements[sectionId].Children.Count >= 24)
            throw new AgentProviderException("demo_output_limit", "This document has no room for another demo feedback block.");
        var id = "demo-direction";
        for (var suffix = 1; spec.Elements.ContainsKey(id); suffix++)
            id = $"demo-direction-{suffix}";
        spec.Elements.Add(id, new("Text", Props(new { anchorId = id, text = "Proposed revision." }), []));
        spec.Elements[sectionId].Children.Add(id);
        return id;
    }

    private static IEnumerable<string> Descendants(UiSpec spec, string id)
    {
        foreach (var child in spec.Elements[id].Children)
        {
            yield return child;
            foreach (var descendant in Descendants(spec, child))
                yield return descendant;
        }
    }

    private static ReviewerResponse Review(AgentPromptContext input)
    {
        if (input.Proposals is null || input.Proposals.Count is < 1 or > 3)
            throw Invalid("The demo reviewer did not receive actual producer proposals.");
        foreach (var artifact in input.Proposals)
        {
            if (artifact is null)
                throw Invalid("The demo reviewer received an empty proposal.");
            SpecValidator.Validate(artifact.Spec, "artifact");
        }
        var assignment = input.Assignment ?? throw Invalid("A reviewer needs its declared purpose.");
        var summary = $"Demo review by {assignment.Name} of {input.Proposals.Count} proposal(s) for '{Compact(input.Objective, 180)}': " +
            $"{assignment.Role} " +
            $"the draft includes {input.Answers.Count} saved answer(s)" +
            (input.Feedback is null ? ". " : $" and reflects the direction '{Compact(input.Feedback.Text, 350)}'. ") +
            "Check the assumptions and tradeoffs. Nothing has been accepted or executed; the final decision remains yours.";
        return new(Compact(summary, input.ReviewCharacterLimit));
    }

    private static AgentAssignment Reuse(AgentPromptContext input, string id, string name, string purpose, string kind)
    {
        var previous = input.ExistingAgents.FirstOrDefault(agent => agent.Id == id)
            ?? input.ExistingAgents.FirstOrDefault(agent => string.Equals(agent.Name, name, StringComparison.OrdinalIgnoreCase));
        return new(previous?.Id ?? id, name, purpose, kind);
    }

    private static DemoDecisions ReadDecisions(AgentPromptContext input)
    {
        var detail = Has(input.Objective, "full playbook", "comprehensive", "detailed") ? 5 :
            Has(input.Objective, "brief", "concise", "short", "one-page") ? 1 : 3;
        if (input.Answers.TryGetValue("detail", out var detailText))
        {
            var value = Number(detailText);
            if (value is < 1 or > 5 || value != decimal.Truncate(value))
                throw Invalid("The demo detail answer must be a whole number from one to five.");
            detail = (int)value;
        }
        var stakeholders = DeclaredStakeholders(input.Objective);
        if (input.Answers.TryGetValue("stakeholders", out var stakeholderText))
        {
            try
            {
                stakeholders = JsonSerializer.Deserialize<string[]>(stakeholderText, JsonDefaults.Options)
                    ?? throw Invalid("The stakeholder answer must be a JSON array.");
            }
            catch (JsonException exception)
            {
                throw new AgentProviderException("invalid_request", "The stakeholder answer must be a JSON array of option IDs.", exception);
            }
            if (stakeholders.Length is < 1 or > 6 || stakeholders.Any(key => key is null || !Stakeholders.ContainsKey(key)) ||
                stakeholders.Distinct(StringComparer.Ordinal).Count() != stakeholders.Length)
                throw Invalid("The stakeholder answer contains missing, duplicate, or unknown options.");
        }
        var review = Regex.IsMatch(PositiveIntent(input.Objective), @"\b(?:review|risk|audit)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        if (input.Answers.TryGetValue("review", out var reviewText))
            review = reviewText switch { "true" => true, "false" => false, _ => throw Invalid("The review preference must be true or false.") };
        if (input.Answers.TryGetValue("quantity", out var quantity))
        {
            var value = Number(quantity);
            if (value is < 1 or > 100_000 || value != decimal.Truncate(value))
                throw Invalid("The people count must be a whole number within the displayed range.");
        }
        if (input.Answers.TryGetValue("budget", out var budget))
        {
            var value = Number(budget);
            if (value is < 0 or > 1_000_000 || value % 100 != 0)
                throw Invalid("The budget must use the displayed bounds and increments.");
        }
        if (input.Answers.TryGetValue("deadline", out var deadline) &&
            !DateOnly.TryParseExact(deadline, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            throw Invalid("The deadline answer must be a valid ISO date.");
        return new(detail, stakeholders, review);
    }

    private static decimal Number(string text) =>
        decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)
            ? value : throw Invalid("A numeric answer must use an invariant decimal string.");

    private static string AnswerText(string key, string value, DemoDecisions decisions) => key switch
    {
        "detail" => $"Detail: {DetailLabels[decisions.Detail - 1]} ({decisions.Detail}/5)",
        "stakeholders" => "Stakeholders: " + string.Join(", ", decisions.StakeholderIds.Select(id => Stakeholders[id])),
        "review" => decisions.Review ? "Specialist review requested; human acceptance is still required." :
            "No specialist review requested; human acceptance is still required.",
        "deadline" => $"Deadline: {value}",
        "quantity" => $"People to plan for: {value}",
        "budget" => $"Planning budget: {value}",
        _ => $"{key}: {value}"
    };

    private static bool IsSimpleCompleteTask(string objective) =>
        Regex.IsMatch(objective, @"\b(?:one|single)(?:[-\s]+(?:friendly|short|concise|brief|warm|clear)){0,2}[-\s]+(?:sentence|line)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)) &&
        Has(objective, "thank", "thanks");

    private static bool HasSpecifiedDetail(string objective) =>
        Has(objective, "brief", "concise", "short", "one-page", "full playbook", "comprehensive", "detailed", "one-sentence");

    private static bool HasKnownAudience(string objective) =>
        Has(objective, "to the team", "for the team", "leadership", "customers", "volunteers", "to my", "for my");

    private static bool HasDate(string objective) =>
        Regex.Matches(objective, @"\b\d{4}-\d{2}-\d{2}\b", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))
            .Any(match => DateOnly.TryParseExact(match.Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _));

    private static bool HasQuantity(string objective) => Regex.IsMatch(objective,
        @"\b\d+\s+(?:attendees?|participants?|people|guests?|volunteers?)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    private static bool HasBudget(string objective) => Regex.IsMatch(objective,
        @"(?:\b(?:budget|cost)\s*(?:of|is|:)?\s*[$]?\s*\d|[$]\s*\d|\b(?:USD|EUR|GBP)\s*\d)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    private static string[] DeclaredStakeholders(string objective)
    {
        var selected = Stakeholders.Keys.Where(key =>
            Has(objective, $"{key} team", $"{key} department", $"for {key}", $"with {key}")).ToHashSet(StringComparer.Ordinal);
        var names = string.Join("|", Stakeholders.Keys);
        foreach (Match match in Regex.Matches(objective, $@"\b({names})\s*(?:,|and|&)\s*({names})\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
        {
            selected.Add(match.Groups[1].Value.ToLowerInvariant());
            selected.Add(match.Groups[2].Value.ToLowerInvariant());
        }
        return Stakeholders.Keys.Where(selected.Contains).ToArray();
    }
    private static bool Has(string text, params string[] fragments) =>
        fragments.Any(fragment => text.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    private static string PositiveIntent(string objective) => Regex.Replace(objective,
        @"\b(?:no|without|do\s+not|don't)\s+(?:(?:an?|external|independent|specialist|additional|agent)\s+)*(?:research|review|audit)(?:\s*(?:,|or|and)\s*(?:(?:an?|external|independent|specialist|additional|agent)\s+)*(?:research|review|audit))*\b",
        "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    private static TaskProfile Profile(string objective)
    {
        objective = PositiveIntent(objective);
        if (Has(objective, "email", "message", "announcement", "newsletter", "article"))
            return new("communications", "communications-writer", "Communications writer", "Draft clear language for the intended audience.",
                "audience-editor", "Audience editor", "Refine the existing candidate for its audience and level of detail.",
                "clarity-reviewer", "Clarity reviewer", "Check tone, clarity, and unsupported claims in the actual draft.");
        if (Has(objective, "research", "compare", "evaluate", "options"))
            return new("research", "research-synthesist", "Research synthesist", "Organize the supplied information without inventing sources.",
                "comparison-designer", "Comparison designer", "Refine the candidate's alternatives and decision structure.",
                "evidence-reviewer", "Evidence reviewer", "Flag assumptions and claims that still need evidence.");
        if (Has(objective, "event", "workshop", "offsite"))
            return new("event", "event-planner", "Event planner", "Draft the event plan using the chosen scale and timing.",
                "logistics-editor", "Logistics editor", "Refine the existing plan's responsibilities and practical constraints.",
                "feasibility-reviewer", "Feasibility reviewer", "Check whether the proposed sequence fits the supplied constraints.");
        if (Has(objective, "launch", "rollout", "go-to-market"))
            return new("launch", "launch-strategist", "Launch strategist", "Draft a phased launch plan grounded in the saved decisions.",
                "stakeholder-editor", "Stakeholder editor", "Refine the candidate for the selected teams and their handoffs.",
                "launch-risk-reviewer", "Launch risk reviewer", "Check dependencies, assumptions, and launch tradeoffs.");
        return new("planning", "delivery-planner", "Delivery planner", "Draft a practical next-step plan for the requested outcome.",
            "implementation-editor", "Implementation editor", "Refine the current candidate into actionable responsibilities.",
            "readiness-reviewer", "Readiness reviewer", "Identify remaining assumptions and decisions before execution.");
    }

    private sealed record DemoDecisions(int Detail, string[] StakeholderIds, bool Review);
    private sealed record TaskProfile(
        string Family, string ProducerId, string ProducerName, string ProducePurpose,
        string EditorId, string EditorName, string EditPurpose,
        string ReviewerId, string ReviewerName, string ReviewPurpose);

    private static string AppendDirection(string existing, string direction, int limit)
    {
        const string separator = "\n\nDemo direction: ";
        var available = limit - existing.Length - separator.Length;
        if (available < 4)
            throw new AgentProviderException("demo_output_limit",
                "The selected field is too long for this demo revision. Shorten it or choose another block.");
        return existing + separator + Compact(direction, available);
    }

    private static string Compact(string text, int limit)
    {
        if (text.Length <= limit) return text;
        var end = limit - 3;
        if (end > 0 && char.IsHighSurrogate(text[end - 1])) end--;
        return text[..end] + "...";
    }

    private static string SessionId(string workspace, string agent) =>
        "demo-" + Digest(JsonSerializer.Serialize(new[] { workspace, agent }, JsonDefaults.Options))[..32];

    private static string Digest(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static JsonObject Props<T>(T value) =>
        JsonSerializer.SerializeToNode(value, JsonDefaults.Options)?.AsObject()
        ?? throw Invalid("The demo could not construct its structured output.");

    private static AgentProviderException Invalid(string message) => new("invalid_request", message);
}
