namespace Workspace.Server.Orchestration;

internal static class ClarificationQualityPolicy
{
    public static void ValidateNewForm(UiSpec spec)
    {
        var questions = spec.Elements.Values
            .Where(element => element.Type.EndsWith("Question", StringComparison.Ordinal)).ToArray();
        if (questions.Count(question => question.Props["required"]!.GetValue<bool>()) > 4)
            throw new WorkspaceException("invalid_clarification",
                "The new form asks too many required decisions. Use at most four.");
        if (questions.Count(question => question.Type == "TextQuestion") > 2)
            throw new WorkspaceException("invalid_clarification",
                "The new form relies on too many open text questions. Prefer the appropriate visual controls.");
    }
}
