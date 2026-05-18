using System.Diagnostics.CodeAnalysis;

namespace AgentNotes.Core;

/// <summary>Process-wide settings loaded at MCP host startup (<c>--config</c>).</summary>
public static class AgentNotesRuntime
{
    private static LocalSettings? s_settings;

    /// <summary>Absolute path passed to <c>--config</c> at startup (MCP host).</summary>
    public static string? ConfigFilePath { get; private set; }

    public static bool IsConfigured => s_settings is not null;

    public static LocalSettings Settings =>
        s_settings ?? throw new InvalidOperationException("Local settings are not loaded. MCP 2.0 requires --config at startup.");

    public static void Initialize(LocalSettings settings, string? configFilePath = null)
    {
        s_settings = settings ?? throw new ArgumentNullException(nameof(settings));
        ConfigFilePath = string.IsNullOrWhiteSpace(configFilePath)
            ? null
            : Path.GetFullPath(configFilePath.Trim());
    }

    /// <summary>Clears loaded TOML (host reload, empty config path, or test isolation).</summary>
    public static void ClearConfiguration()
    {
        s_settings = null;
        ConfigFilePath = null;
    }

    /// <summary>Test isolation alias for <see cref="ClearConfiguration"/>.</summary>
    public static void ResetForTests() => ClearConfiguration();

    public static bool TryGetPrimaryKnowledgeRoot([NotNullWhen(true)] out string? root)
    {
        if (s_settings is null)
        {
            root = null;
            return false;
        }

        root = s_settings.PrimaryKnowledgeRoot;
        return true;
    }
}
