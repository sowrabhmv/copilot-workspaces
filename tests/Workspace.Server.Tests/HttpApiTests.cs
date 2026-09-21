using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Workspace.Server;

namespace Workspace.Server.Tests;

public sealed class HttpApiTests
{
    [Fact]
    public async Task RealDemoWorkflowSupportsClarificationFeedbackHumanEditingAndRestart()
    {
        var directory = Path.Combine(Path.GetTempPath(), "workspaces-http-tests", Guid.NewGuid().ToString("N"));
        string workspaceId;
        int humanRevision;
        try
        {
            await using (var factory = new LocalApplication(directory))
            using (var client = factory.CreateClient())
            {
                await Connect(client);
                var response = await client.PostAsJsonAsync("/api/workspaces",
                    new CreateWorkspaceRequest("Plan a small community workshop with explicit tradeoffs and a clear success measure.", "demo"));
                Assert.Equal(HttpStatusCode.Created, response.StatusCode);
                var workspace = await Document(response);
                workspaceId = workspace.Id;
                workspace = await WaitFor(client, workspaceId, w => w.Clarification is not null);
                var answers = ServiceTestData.AnswersFor(workspace.Clarification!);
                var answered = await client.PostAsJsonAsync($"/api/workspaces/{workspaceId}/answers",
                    new AnswerRequest(workspace.ClarificationId!, answers));
                Assert.Equal(HttpStatusCode.OK, answered.StatusCode);
                workspace = await WaitFor(client, workspaceId, w => w.Artifacts.Count > 0);
                var artifact = workspace.Artifacts[0];
                var head = artifact.Revisions.Single(r => r.Revision == artifact.CurrentRevision);
                SpecValidator.Validate(head.Spec, "artifact");
                var block = head.Spec.Elements.First(e => e.Value.Type == "Text").Key;
                var requests = new[]
                {
                    new FeedbackRequest("Make this section more concise.", Guid.NewGuid().ToString(), artifact.Id, block, head.Revision),
                    new FeedbackRequest("Make the audience explicit without inventing evidence.", Guid.NewGuid().ToString(), artifact.Id, block, head.Revision)
                };
                var queuedResponses = await Task.WhenAll(requests.Select(request =>
                    client.PostAsJsonAsync($"/api/workspaces/{workspaceId}/feedback", request)));
                Assert.All(queuedResponses, queued => Assert.Equal(HttpStatusCode.OK, queued.StatusCode));
                foreach (var queued in queuedResponses) queued.Dispose();
                workspace = await WaitFor(client, workspaceId,
                    w => w.Feedback.Count == 2 && w.Jobs.Count(j => j.Kind == "feedback" && j.Status == "completed") == 2);
                artifact = workspace.Artifacts.Single(a => a.Id == artifact.Id);
                Assert.True(artifact.Revisions.Count >= 3);
                Assert.Null(artifact.AcceptedRevision);
                var review = await client.PostAsJsonAsync($"/api/workspaces/{workspaceId}/artifacts/{artifact.Id}/review",
                    new ReviewRequest(artifact.CurrentRevision, "accept"));
                Assert.Equal(HttpStatusCode.OK, review.StatusCode);
                workspace = await Document(review);
                artifact = workspace.Artifacts.Single(a => a.Id == artifact.Id);
                var textBlock = artifact.Revisions.Single(r => r.Revision == artifact.CurrentRevision)
                    .Spec.Elements.First(e => e.Value.Type == "Text").Key;
                var edit = await client.PostAsJsonAsync($"/api/workspaces/{workspaceId}/artifacts/{artifact.Id}/edit",
                    new EditRequest(artifact.CurrentRevision, textBlock, "A human-authored decision that must survive restart."));
                Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
                workspace = await Document(edit);
                humanRevision = workspace.Artifacts.Single(a => a.Id == artifact.Id).CurrentRevision;
                Assert.Contains(workspace.Decisions, decision => decision.Kind == "edit");
                Assert.All(workspace.Feedback, note => Assert.Equal(head.Revision, note.Revision));

                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using var eventRequest = new HttpRequestMessage(HttpMethod.Get,
                    $"/api/workspaces/{workspaceId}/events?after={workspace.EventSequence}");
                using var events = await client.SendAsync(eventRequest, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                Assert.Equal("text/event-stream", events.Content.Headers.ContentType?.MediaType);
                using var reader = new StreamReader(await events.Content.ReadAsStreamAsync(timeout.Token));
                var lines = new List<string>();
                while (!lines.Any(line => line.StartsWith("data:", StringComparison.Ordinal)))
                    lines.Add(await reader.ReadLineAsync(timeout.Token) ?? throw new InvalidOperationException("SSE ended before its initial event."));
                Assert.Contains(lines, line => line.StartsWith("event: workspace", StringComparison.Ordinal));
            }

            await using (var restarted = new LocalApplication(directory))
            using (var client = restarted.CreateClient())
            {
                var workspace = await client.GetFromJsonAsync<WorkspaceDocument>($"/api/workspaces/{workspaceId}", JsonDefaults.Options);
                Assert.NotNull(workspace);
                Assert.Contains(workspace.Artifacts.SelectMany(a => a.Revisions),
                    revision => revision.Revision == humanRevision && revision.Source == "human" && revision.Status == "accepted");
                Assert.Equal(2, workspace.Feedback.Count);
            }
        }
        finally
        {
            if (Directory.Exists(directory)) await DeleteTestDirectory(directory);
        }
    }

    [Fact]
    public async Task HttpBoundaryRejectsMissingTokensForeignOriginsAndUnrecognizedFields()
    {
        var directory = Path.Combine(Path.GetTempPath(), "workspaces-http-tests", Guid.NewGuid().ToString("N"));
        try
        {
            await using var factory = new LocalApplication(directory);
            using var client = factory.CreateClient();
            var noToken = await client.PostAsJsonAsync("/api/workspaces", new CreateWorkspaceRequest("Test task", "demo"));
            Assert.Equal(HttpStatusCode.Forbidden, noToken.StatusCode);
            await Connect(client);
            client.DefaultRequestHeaders.Add("Origin", "https://untrusted.example");
            var untrusted = await client.PostAsJsonAsync("/api/workspaces", new CreateWorkspaceRequest("Test task", "demo"));
            Assert.Equal(HttpStatusCode.Forbidden, untrusted.StatusCode);
            client.DefaultRequestHeaders.Remove("Origin");
            var extra = await client.PostAsync("/api/workspaces", new StringContent(
                """{"objective":"Test task","provider":"demo","model":"an-unapproved-model"}""", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, extra.StatusCode);
            var empty = await client.PostAsJsonAsync("/api/workspaces", new CreateWorkspaceRequest("", "demo"));
            Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
            var tasks = await client.GetFromJsonAsync<List<WorkspaceSummary>>("/api/workspaces");
            Assert.Empty(tasks!);
            var unknown = await client.GetAsync("/api/not-a-real-endpoint");
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
            Assert.Contains("endpoint_not_found", await unknown.Content.ReadAsStringAsync());
        }
        finally
        {
            if (Directory.Exists(directory)) await DeleteTestDirectory(directory);
        }
    }

    [Fact]
    public async Task CurrentQuestionsCanBeExplicitlyRegeneratedWithoutSpawningSpecialists()
    {
        var directory = Path.Combine(Path.GetTempPath(), "workspaces-http-tests", Guid.NewGuid().ToString("N"));
        try
        {
            await using var factory = new LocalApplication(directory);
            using var client = factory.CreateClient();
            await Connect(client);
            using var created = await client.PostAsJsonAsync("/api/workspaces",
                new CreateWorkspaceRequest("Plan an internal product launch. Help shape the audience and detail.", "demo"));
            var workspace = await Document(created);
            Assert.Single(workspace.Agents);
            workspace = await WaitFor(client, workspace.Id, value => value.Clarification is not null);
            var originalForm = workspace.ClarificationId;
            Assert.Contains(workspace.Clarification!.Elements.Values, element => element.Type == "SliderQuestion");
            Assert.Contains(workspace.Clarification.Elements.Values, element => element.Type == "MultiChoiceQuestion");
            using var requested = await client.PostAsJsonAsync($"/api/workspaces/{workspace.Id}/clarification/refresh",
                new RefreshClarificationRequest(originalForm!));
            Assert.Equal(HttpStatusCode.OK, requested.StatusCode);
            var acknowledged = await Document(requested);
            Assert.Equal(originalForm, acknowledged.ClarificationId);
            workspace = await WaitFor(client, workspace.Id,
                value => value.ClarificationId != originalForm && value.Jobs.Last().Status == "completed");
            Assert.Equal("clarify", workspace.Jobs.Last().Kind);
            Assert.Single(workspace.Agents, agent => agent.Assigned);
            var questions = workspace.Clarification!.Elements.Values.Where(element => SpecValidator.IsQuestion(element.Type)).ToArray();
            Assert.InRange(questions.Count(question => question.Props["required"]!.GetValue<bool>()), 0, 4);
            Assert.InRange(questions.Count(question => question.Type == "TextQuestion"), 0, 2);
            using var stale = await client.PostAsJsonAsync($"/api/workspaces/{workspace.Id}/answers",
                new AnswerRequest(originalForm!, ServiceTestData.AnswersFor(workspace.Clarification)));
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        }
        finally
        {
            if (Directory.Exists(directory)) await DeleteTestDirectory(directory);
        }
    }

    private static async Task Connect(HttpClient client)
    {
        var bootstrap = await client.GetFromJsonAsync<JsonElement>("/api/bootstrap");
        Assert.Equal("auto", bootstrap.GetProperty("model").GetString());
        Assert.Equal(JsonValueKind.Null, bootstrap.GetProperty("reasoningEffort").ValueKind);
        client.DefaultRequestHeaders.Add("X-Workspace-Token", bootstrap.GetProperty("csrfToken").GetString());
    }

    private static async Task DeleteTestDirectory(string directory)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 4)
            {
                // Windows can briefly retain a just-closed in-process test-host handle.
                await Task.Delay(50 * (attempt + 1));
            }
        }
    }

    private static async Task<WorkspaceDocument> Document(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<WorkspaceDocument>(JsonDefaults.Options)
        ?? throw new InvalidOperationException("The API returned no workspace.");

    private static async Task<WorkspaceDocument> WaitFor(
        HttpClient client, string id, Func<WorkspaceDocument, bool> predicate)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(20))
        {
            var workspace = await client.GetFromJsonAsync<WorkspaceDocument>($"/api/workspaces/{id}", JsonDefaults.Options)
                ?? throw new InvalidOperationException("The workspace disappeared.");
            Assert.DoesNotContain(workspace.Jobs, job => job.Status == "failed");
            if (predicate(workspace)) return workspace;
            await Task.Delay(50);
        }
        throw new TimeoutException("The real demo workflow did not reach its expected state.");
    }

    private sealed class LocalApplication(string dataDirectory) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
            {
                var options = new WorkspaceOptions
                {
                    DataDirectory = dataDirectory,
                    DemoDelayMilliseconds = 1,
                    ProviderTimeoutSeconds = 10
                };
                services.RemoveAll<WorkspaceOptions>();
                services.RemoveAll<IOptions<WorkspaceOptions>>();
                services.AddSingleton(options);
                services.AddSingleton<IOptions<WorkspaceOptions>>(Options.Create(options));
            });
        }
    }
}
