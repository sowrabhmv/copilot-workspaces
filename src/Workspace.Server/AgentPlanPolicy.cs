namespace Workspace.Server;

public static class AgentPlanPolicy
{
    public const string CoordinatorId = "planner";

    public static void ValidateAssignments(IReadOnlyList<AgentAssignment>? assignments)
    {
        if (assignments is null || assignments.Count > WorkspaceOptions.MaximumSpecialists)
            throw new WorkspaceException("invalid_agent_plan", "Choose zero to four specialists for this task.");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var assignment in assignments)
        {
            if (assignment is null || !SpecValidator.IsId(assignment.Id) ||
                assignment.Id.Equals(CoordinatorId, StringComparison.OrdinalIgnoreCase) ||
                !ids.Add(assignment.Id))
                throw new WorkspaceException("invalid_agent_plan", "Specialists need distinct, safe identities separate from the coordinator.");
            if (string.IsNullOrWhiteSpace(assignment.Name) || assignment.Name.Length > 80 ||
                string.IsNullOrWhiteSpace(assignment.Role) || assignment.Role.Length > 1000 ||
                assignment.Kind is not ("produce" or "review"))
                throw new WorkspaceException("invalid_agent_plan", "Every specialist needs a short name, a clear purpose, and a supported work type.");
        }
    }

    public static void ApplyRoster(WorkspaceDocument document, IReadOnlyList<AgentAssignment> assignments)
    {
        ValidateAssignments(assignments);
        var saved = document.Agents.ToDictionary(agent => agent.Id, StringComparer.Ordinal);
        var totalSpecialists = saved.Keys.Where(id => id != CoordinatorId)
            .Union(assignments.Select(agent => agent.Id), StringComparer.Ordinal).Count();
        if (totalSpecialists > WorkspaceOptions.MaximumSavedSpecialists)
            throw new WorkspaceException("agent_limit",
                "This workspace has twelve saved specialist identities. Reuse an appropriate existing specialist instead of creating more.");
        if (!saved.TryGetValue(CoordinatorId, out var coordinator))
            throw new WorkspaceException("invalid_agent_plan", "The workspace coordinator is missing.", 500);

        foreach (var agent in document.Agents) agent.Assigned = agent.Id == CoordinatorId;
        coordinator.Name = "Coordinator";
        coordinator.Role = "Understands the task and recruits help only when needed";
        var current = new List<AgentState> { coordinator };
        foreach (var assignment in assignments)
        {
            if (!saved.TryGetValue(assignment.Id, out var agent))
                agent = new() { Id = assignment.Id, Name = assignment.Name, Role = assignment.Role };
            agent.Name = assignment.Name;
            agent.Role = assignment.Role;
            agent.Assigned = true;
            agent.Status = "idle";
            agent.Message = "Recruited for this task; waiting to start.";
            agent.ActiveJobId = null;
            current.Add(agent);
            document.Layout.Positions.TryAdd($"agent-{agent.Id}", new(550 + (current.Count - 1) * 320, 64));
        }
        current.AddRange(document.Agents.Where(agent => !agent.Assigned));
        document.Agents = current;
    }
}
