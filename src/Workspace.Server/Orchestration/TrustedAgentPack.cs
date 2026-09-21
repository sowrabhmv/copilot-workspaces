using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Workspace.Server.Orchestration;

internal sealed class TrustedAgentPack
{
    private static readonly Dictionary<string, string> ExpectedFiles = new(StringComparer.Ordinal)
    {
        ["common"] = ".github/instructions/workspace-contract.instructions.md",
        ["planner"] = ".github/prompts/planner.prompt.md",
        ["producer"] = ".github/prompts/producer.prompt.md",
        ["reviewer"] = ".github/prompts/reviewer.prompt.md"
    };

    private readonly Dictionary<string, string> _instructions;

    private TrustedAgentPack(Dictionary<string, string> instructions) => _instructions = instructions;

    public string ForRole(string role) => _instructions["common"] + "\n\n" + _instructions[role];

    public static TrustedAgentPack Load(string directory)
    {
        try
        {
            var manifestBytes = ReadBounded(Path.Combine(directory, "manifest.json"), 16 * 1024);
            var manifest = JsonSerializer.Deserialize<PackManifest>(manifestBytes, JsonDefaults.Options)
                ?? throw Invalid("The prepared pack manifest is empty.");
            if (manifest.FormatVersion != 1 || manifest.Name != "workspace-agent-pack" ||
                manifest.Version != "0.2.0" || manifest.ApmVersion != "0.31.0" ||
                manifest.Files is null || manifest.Files.Count != ExpectedFiles.Count)
                throw Invalid("The prepared pack has an unsupported identity or version.");

            VerifyHash(ReadBounded(Path.Combine(directory, "apm.lock.yaml"), 512 * 1024), manifest.LockSha256);
            var instructions = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (role, expectedPath) in ExpectedFiles)
            {
                if (!manifest.Files.TryGetValue(role, out var file) || file is null || file.Path != expectedPath)
                    throw Invalid("The prepared pack is missing a required, allowlisted instruction file.");
                var bytes = ReadBounded(
                    Path.Combine(directory, expectedPath.Replace('/', Path.DirectorySeparatorChar)), 64 * 1024);
                VerifyHash(bytes, file.Sha256);
                var text = new UTF8Encoding(false, true).GetString(bytes);
                if (string.IsNullOrWhiteSpace(text))
                    throw Invalid("A prepared instruction file is empty.");
                instructions.Add(role, text);
            }
            return new(instructions);
        }
        catch (JsonException exception)
        {
            throw new AgentProviderException("agent_pack_invalid",
                "The trusted agent pack manifest is invalid. Prepare the pack again.", exception);
        }
        catch (DecoderFallbackException exception)
        {
            throw new AgentProviderException("agent_pack_invalid",
                "The trusted agent pack contains invalid UTF-8 text. Prepare the pack again.", exception);
        }
        catch (IOException exception)
        {
            throw new AgentProviderException("agent_pack_unavailable",
                "The prepared agent pack could not be read. Run scripts\\prepare-agent-pack.ps1.", exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new AgentProviderException("agent_pack_unavailable",
                "The prepared agent pack is not readable by this application.", exception);
        }
    }

    private static byte[] ReadBounded(string path, int limit)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is <= 0 || stream.Length > limit)
            throw Invalid("A prepared agent-pack file is empty or exceeds its size limit.");
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static void VerifyHash(byte[] bytes, string? expected)
    {
        var actual = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (expected is null || expected.Length != 64 ||
            !string.Equals(actual, expected, StringComparison.Ordinal))
            throw Invalid("The prepared agent pack has changed since preparation. Prepare it again.");
    }

    private static AgentProviderException Invalid(string message) => new("agent_pack_invalid", message);

    private sealed class PackManifest
    {
        public required int FormatVersion { get; init; }
        public required string Name { get; init; }
        public required string Version { get; init; }
        public required string ApmVersion { get; init; }
        public required string LockSha256 { get; init; }
        public required Dictionary<string, PackFile> Files { get; init; }
    }

    private sealed class PackFile
    {
        public required string Path { get; init; }
        public required string Sha256 { get; init; }
    }
}
