using System.Globalization;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;

namespace Workspace.Server;

public static class HttpApi
{
    public static void MapWorkspaceApi(this WebApplication app)
    {
        app.MapGet("/api/health", () => new { status = "ok" });
        app.MapGet("/api/bootstrap", (LocalRequestGuard guard) => new
        {
            csrfToken = guard.Token,
            model = WorkspaceOptions.Model,
            reasoningEffort = WorkspaceOptions.ReasoningEffort,
            providers = new[]
            {
                new { id = "copilot", label = "Copilot CLI (live)" },
                new { id = "demo", label = "Demo (no model calls)" }
            },
            version = "0.1.0"
        });
        app.MapGet("/api/provider", async (IEnumerable<IAgentProvider> providers, CancellationToken token) =>
            await providers.Single(p => p.Name == "copilot").GetStatusAsync(token));
        app.MapGet("/api/workspaces", (WorkspaceStore store) => store.List().Select(WorkspaceStore.Summary).ToArray());
        app.MapPost("/api/workspaces", (CreateWorkspaceRequest request, WorkspaceService service) =>
        {
            var document = service.Create(request);
            return Results.Created($"/api/workspaces/{document.Id}", document);
        });
        app.MapGet("/api/workspaces/{id}", (string id, WorkspaceStore store) => store.Get(id));
        app.MapPost("/api/workspaces/{id}/answers",
            (string id, AnswerRequest request, WorkspaceService service) => service.Answer(id, request));
        app.MapPost("/api/workspaces/{id}/clarification/refresh",
            (string id, RefreshClarificationRequest request, WorkspaceService service) =>
                service.RefreshClarification(id, request));
        app.MapPost("/api/workspaces/{id}/feedback",
            (string id, FeedbackRequest request, WorkspaceService service) => service.Feedback(id, request));
        app.MapPost("/api/workspaces/{id}/jobs/{jobId}/cancel",
            (string id, string jobId, WorkspaceService service, WorkspaceCoordinator coordinator) =>
            {
                var document = service.Cancel(id, jobId);
                coordinator.CancelActive(id, jobId);
                return document;
            });
        app.MapPost("/api/workspaces/{id}/jobs/{jobId}/retry",
            (string id, string jobId, WorkspaceService service) => service.Retry(id, jobId));
        app.MapPost("/api/workspaces/{id}/agents/{agentId}/reset-session",
            (string id, string agentId, WorkspaceService service) => service.ResetSession(id, agentId));
        app.MapPost("/api/workspaces/{id}/artifacts/{artifactId}/review",
            (string id, string artifactId, ReviewRequest request, WorkspaceService service) =>
                service.Review(id, artifactId, request));
        app.MapPost("/api/workspaces/{id}/artifacts/{artifactId}/edit",
            (string id, string artifactId, EditRequest request, WorkspaceService service) =>
                service.Edit(id, artifactId, request));
        app.MapPut("/api/workspaces/{id}/layout",
            (string id, CanvasLayout request, WorkspaceService service) => service.Layout(id, request));
        app.MapGet("/api/workspaces/{id}/events", (string id, HttpContext context, WorkspaceStore store) =>
        {
            store.Get(id);
            var cursor = context.Request.Headers["Last-Event-ID"].FirstOrDefault()
                         ?? context.Request.Query["after"].FirstOrDefault() ?? "0";
            if (!long.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var after))
                throw new WorkspaceException("invalid_cursor", "The event cursor must be a nonnegative integer.");
            context.Response.Headers.CacheControl = "no-cache, no-transform";
            context.Response.Headers["X-Accel-Buffering"] = "no";
            return TypedResults.ServerSentEvents(Stream(store, id, after, context.RequestAborted));
        });
        app.Map("/api/{**unmatched}", () => Results.NotFound(new ApiError("endpoint_not_found", "This API endpoint does not exist.")));
    }

    public static async IAsyncEnumerable<SseItem<WorkspaceEvent>> Stream(
        WorkspaceStore store, string workspaceId, long after,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var document = store.Get(workspaceId);
        if (after > document.EventSequence) after = document.EventSequence;
        var initialCursor = after;
        yield return new(new(initialCursor, workspaceId, "connected",
            "Connected. Refresh the canonical workspace state.", null, null, DateTimeOffset.UtcNow), "workspace")
        {
            EventId = initialCursor.ToString(CultureInfo.InvariantCulture),
            ReconnectionInterval = TimeSpan.FromSeconds(2)
        };
        var heartbeat = DateTimeOffset.UtcNow;
        while (!cancellationToken.IsCancellationRequested)
        {
            foreach (var item in store.Events(workspaceId, after))
            {
                after = item.Sequence;
                yield return new(item, "workspace") { EventId = item.Sequence.ToString(CultureInfo.InvariantCulture) };
                heartbeat = DateTimeOffset.UtcNow;
            }
            if (DateTimeOffset.UtcNow - heartbeat >= TimeSpan.FromSeconds(10))
            {
                yield return new(new(after, workspaceId, "heartbeat", "Connected.",
                    null, null, DateTimeOffset.UtcNow), "heartbeat");
                heartbeat = DateTimeOffset.UtcNow;
            }
            await Task.Delay(300, cancellationToken);
        }
    }
}
