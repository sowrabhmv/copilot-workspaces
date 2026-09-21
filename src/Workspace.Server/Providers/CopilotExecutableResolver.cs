namespace Workspace.Server.Providers;

internal static class CopilotExecutableResolver
{
    internal static string Resolve(
        string? configuredPath, Func<string, string?>? readEnvironment = null)
    {
        readEnvironment ??= Environment.GetEnvironmentVariable;
        if (!string.IsNullOrWhiteSpace(configuredPath))
            return RequireExecutable(configuredPath);

        var environmentPath = readEnvironment("COPILOT_CLI_PATH");
        if (!string.IsNullOrWhiteSpace(environmentPath))
            return RequireExecutable(environmentPath);

        foreach (var entry in (readEnvironment("PATH") ?? "").Split(Path.PathSeparator))
        {
            var directory = entry.Trim().Trim('"');
            if (!Path.IsPathFullyQualified(directory)) continue;
            var candidate = Path.Combine(directory, "copilot.exe");
            if (File.Exists(candidate)) return RequireExecutable(candidate);
        }

        var localAppData = readEnvironment("LOCALAPPDATA");
        if (!string.IsNullOrWhiteSpace(localAppData) && Path.IsPathFullyQualified(localAppData))
        {
            var candidate = Path.Combine(localAppData, "Microsoft", "WinGet", "Links", "copilot.exe");
            if (File.Exists(candidate)) return RequireExecutable(candidate);
        }

        throw new AgentProviderException("copilot_executable_missing",
            "Copilot CLI was not found. Install Copilot CLI or configure CopilotExecutable with its absolute executable path.");
    }

    private static string RequireExecutable(string path)
    {
        if (!Path.IsPathFullyQualified(path) ||
            !string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new AgentProviderException("copilot_executable_invalid",
                "CopilotExecutable must be an absolute Windows executable path, not a shell command or .cmd shim.");

        string fullPath;
        try { fullPath = Path.GetFullPath(path); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new AgentProviderException("copilot_executable_invalid",
                "The configured Copilot executable path is invalid.");
        }

        if (!File.Exists(fullPath))
            throw new AgentProviderException("copilot_executable_missing",
                "The configured Copilot executable does not exist. Correct CopilotExecutable before retrying.");
        return fullPath;
    }
}
