using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Workspace.Server;

public sealed class WorkspaceStore : IDisposable
{
    private readonly object _gate = new();
    private readonly string _connectionString;
    private readonly FileStream _processLock;
    private bool _disposed;

    public WorkspaceStore(WorkspaceOptions options)
    {
        var directory = Path.GetFullPath(options.DataDirectory);
        Directory.CreateDirectory(directory);
        _processLock = new FileStream(Path.Combine(directory, "server.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(directory, "workspaces.db"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            DefaultTimeout = 5,
            Pooling = false
        }.ToString();
        try
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version;";
            var version = Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
            if (version is not (0 or 1))
                throw new InvalidDataException("This workspace database uses an unsupported schema version.");
            command.CommandText = """
                PRAGMA journal_mode=WAL;
                PRAGMA synchronous=FULL;
                CREATE TABLE IF NOT EXISTS workspaces (
                    id TEXT PRIMARY KEY,
                    payload TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    version INTEGER NOT NULL
                );
                CREATE TABLE IF NOT EXISTS events (
                    sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                    workspace_id TEXT NOT NULL,
                    type TEXT NOT NULL,
                    message TEXT NOT NULL,
                    job_id TEXT,
                    agent_id TEXT,
                    created_at TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS events_workspace_sequence ON events(workspace_id, sequence);
                PRAGMA user_version=1;
                """;
            command.ExecuteNonQuery();
        }
        catch
        {
            _processLock.Dispose();
            throw;
        }
    }

    public WorkspaceDocument Create(string objective, string provider)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            using var count = connection.CreateCommand();
            count.Transaction = transaction;
            count.CommandText = "SELECT COUNT(*) FROM workspaces;";
            if (Convert.ToInt64(count.ExecuteScalar(), CultureInfo.InvariantCulture) >= 100)
                throw new WorkspaceException("workspace_limit", "This local MVP supports up to 100 workspaces.", 409);
            var document = new WorkspaceDocument
            {
                Id = Guid.NewGuid().ToString("N"),
                Title = objective.Length <= 70 ? objective : objective[..67] + "...",
                Objective = objective,
                Provider = provider,
                Agents =
                [
                    new()
                    {
                        Id = AgentPlanPolicy.CoordinatorId, Name = "Coordinator",
                        Role = "Understands the task and recruits help only when needed"
                    }
                ],
                Layout = new(new()
                {
                    ["context"] = new(64, 64),
                    ["agent-planner"] = new(550, 64)
                }, new(24, 24, 0.85))
            };
            document.Jobs.Add(new() { Id = Guid.NewGuid().ToString("N"), Kind = "initial", Order = 1 });
            Save(connection, transaction, document, "workspace.created", "Workspace created. Planning is queued.",
                document.Jobs[0].Id, null);
            transaction.Commit();
            return document;
        }
    }

    public WorkspaceDocument Get(string id)
    {
        lock (_gate)
        {
            using var connection = Open();
            return Read(connection, null, id);
        }
    }

    public List<WorkspaceDocument> List()
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT payload FROM workspaces ORDER BY updated_at DESC, id;";
            using var reader = command.ExecuteReader();
            var documents = new List<WorkspaceDocument>();
            while (reader.Read()) documents.Add(Deserialize(reader.GetString(0)));
            return documents;
        }
    }

    public WorkspaceDocument Update(
        string id, string type, string message, Action<WorkspaceDocument> mutation,
        string? jobId = null, string? agentId = null) =>
        Change(id, type, message, document => { mutation(document); return true; }, jobId, agentId);

    public WorkspaceDocument Change(
        string id, string type, string message, Func<WorkspaceDocument, bool> mutation,
        string? jobId = null, string? agentId = null)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            var document = Read(connection, transaction, id);
            if (mutation(document))
            {
                RefreshStatus(document);
                Save(connection, transaction, document, type, message, jobId, agentId);
            }
            transaction.Commit();
            return document;
        }
    }

    public List<WorkspaceEvent> Events(string workspaceId, long after, int limit = 200)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT sequence, type, message, job_id, agent_id, created_at FROM events
                WHERE workspace_id=$workspace AND sequence>$after ORDER BY sequence LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$workspace", workspaceId);
            command.Parameters.AddWithValue("$after", after);
            command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));
            using var reader = command.ExecuteReader();
            var events = new List<WorkspaceEvent>();
            while (reader.Read())
                events.Add(new(reader.GetInt64(0), workspaceId, reader.GetString(1), reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture)));
            return events;
        }
    }

    public void RecoverInterrupted()
    {
        foreach (var workspace in List())
        {
            Change(workspace.Id, "jobs.interrupted", "Unfinished work needs an explicit retry after restart.", document =>
            {
                var jobs = document.Jobs.Where(j => j.Status is "queued" or "running" or "cancelling").ToList();
                if (jobs.Count == 0) return false;
                foreach (var job in jobs)
                {
                    job.Status = "interrupted";
                    job.ErrorCode = "service_restarted";
                    job.Message = "The service restarted. Your input is saved; retry explicitly to continue.";
                    job.CompletedAt = DateTimeOffset.UtcNow;
                    var feedback = document.Feedback.Find(f => f.Id == job.FeedbackId);
                    if (feedback is not null) feedback.Status = "interrupted";
                }
                foreach (var agent in document.Agents)
                {
                    agent.Status = "idle";
                    agent.ActiveJobId = null;
                    agent.Message = "Previous work was interrupted. Your session reference is saved.";
                }
                return true;
            });
        }
    }

    public static WorkspaceSummary Summary(WorkspaceDocument document) =>
        new(document.Id, document.Title, document.Objective, document.Provider, document.Status,
            document.UpdatedAt,
            document.Jobs.Count(j => j.Status is "queued" or "running" or "cancelling"),
            document.Feedback.Count(f => f.Status is "queued" or "running"),
            document.Artifacts.Sum(a => a.Revisions.Count(r => r.Status == "proposed")),
            document.Artifacts.Count);

    private static void RefreshStatus(WorkspaceDocument document)
    {
        document.Status = document.Jobs.Any(j => j.Status is "running" or "cancelling") ? "working"
            : document.Jobs.Any(j => j.Status == "queued") ? "queued"
            : document.Clarification is not null ? "needsInput"
            : document.Artifacts.Any(a => a.Revisions.Any(r => r.Status == "proposed")) ? "review"
            : document.Jobs.LastOrDefault()?.Status is "failed" or "interrupted" ? "error"
            : "idle";
    }

    private SqliteConnection Open()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private static WorkspaceDocument Read(SqliteConnection connection, SqliteTransaction? transaction, string id)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT payload FROM workspaces WHERE id=$id;";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() is string payload
            ? Deserialize(payload)
            : throw new WorkspaceException("workspace_not_found", "This workspace was not found.", 404);
    }

    private static WorkspaceDocument Deserialize(string payload) =>
        JsonSerializer.Deserialize<WorkspaceDocument>(payload, JsonDefaults.Options)
        ?? throw new InvalidDataException("A workspace record is unreadable. The database has not been reset.");

    private static void Save(
        SqliteConnection connection, SqliteTransaction transaction, WorkspaceDocument document,
        string type, string message, string? jobId, string? agentId)
    {
        document.Revision++;
        document.UpdatedAt = DateTimeOffset.UtcNow;
        using var eventCommand = connection.CreateCommand();
        eventCommand.Transaction = transaction;
        eventCommand.CommandText = """
            INSERT INTO events (workspace_id,type,message,job_id,agent_id,created_at)
            VALUES ($workspace,$type,$message,$job,$agent,$created);
            SELECT last_insert_rowid();
            """;
        eventCommand.Parameters.AddWithValue("$workspace", document.Id);
        eventCommand.Parameters.AddWithValue("$type", type);
        eventCommand.Parameters.AddWithValue("$message", message);
        eventCommand.Parameters.AddWithValue("$job", (object?)jobId ?? DBNull.Value);
        eventCommand.Parameters.AddWithValue("$agent", (object?)agentId ?? DBNull.Value);
        eventCommand.Parameters.AddWithValue("$created", document.UpdatedAt.ToString("O"));
        document.EventSequence = Convert.ToInt64(eventCommand.ExecuteScalar(), CultureInfo.InvariantCulture);
        using var save = connection.CreateCommand();
        save.Transaction = transaction;
        save.CommandText = """
            INSERT INTO workspaces(id,payload,updated_at,version) VALUES ($id,$payload,$updated,$version)
            ON CONFLICT(id) DO UPDATE SET payload=excluded.payload,updated_at=excluded.updated_at,version=excluded.version;
            """;
        save.Parameters.AddWithValue("$id", document.Id);
        save.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(document, JsonDefaults.Options));
        save.Parameters.AddWithValue("$updated", document.UpdatedAt.ToString("O"));
        save.Parameters.AddWithValue("$version", document.Revision);
        save.ExecuteNonQuery();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _processLock.Dispose();
        }
    }
}
