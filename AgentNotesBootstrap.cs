namespace AgentNotes.Core;

/// <summary>CLI / env resolution for MCP 2.0 local TOML.</summary>
public static class AgentNotesBootstrap
{
    public const string ConfigEnvVar = "AGENT_NOTES_CONFIG";

    /// <summary>Exit code when config path is missing.</summary>
    public const int ExitMissingConfig = 2;

    /// <summary>Exit code when config path is set but invalid.</summary>
    public const int ExitInvalidConfig = 1;

    /// <summary>Last successfully loaded config path (<see cref="TryLoadSettings"/>).</summary>
    public static string? LoadedConfigPath { get; private set; }

    public static bool IsStatusOnly(string[] args) =>
        args.Any(static a => a is "--status-only" or "--status_only");

    public static string[] FilterStatusOnlyArgs(string[] args) =>
        args.Where(static a => a is not "--status-only" and not "--status_only").ToArray();

    public static string? ResolveConfigPath(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg is "--config" or "--config-file")
            {
                if (i + 1 >= args.Length)
                    throw new ArgumentException($"Missing path after {arg}.");
                return args[i + 1].Trim();
            }

            const string prefix = "--config=";
            if (arg.StartsWith(prefix, StringComparison.Ordinal))
                return arg[prefix.Length..].Trim();
        }

        var fromEnv = Environment.GetEnvironmentVariable(ConfigEnvVar);
        return string.IsNullOrWhiteSpace(fromEnv) ? null : fromEnv.Trim();
    }

    /// <summary>Load settings for MCP host startup. Returns exit code 0 on success.</summary>
    public static int TryLoadSettings(string[] args, out LocalSettings? settings, out string? errorMessage)
    {
        args = FilterStatusOnlyArgs(args);
        settings = null;
        errorMessage = null;
        LoadedConfigPath = null;
        string? configPath;
        try
        {
            configPath = ResolveConfigPath(args);
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return ExitInvalidConfig;
        }

        if (configPath is null)
        {
            errorMessage =
                "agent-notes-mcp 2.0 requires --config <path.toml> in mcp.json (or AGENT_NOTES_CONFIG). " +
                "Template: knowledge/work/local/agent-notes.workspace.example.toml in the agent-notes KB repository.";
            return ExitMissingConfig;
        }

        try
        {
            LoadedConfigPath = Path.GetFullPath(configPath);
            settings = LocalSettingsLoader.Load(configPath);
            return 0;
        }
        catch (Exception ex)
        {
            errorMessage = $"Failed to load config '{configPath}': {ex.Message}";
            return ExitInvalidConfig;
        }
    }
}
