using System.Diagnostics.CodeAnalysis;

namespace AgentNotes.Core;

/// <summary>Process-wide settings loaded at MCP host startup (<c>--config</c>).</summary>
public static class AgentNotesRuntime
{
    private static LocalSettings? s_settings;

    public static bool IsConfigured => s_settings is not null;

    public static LocalSettings Settings =>
        s_settings ?? throw new InvalidOperationException("Local settings are not loaded. MCP 2.0 requires --config at startup.");

    public static void Initialize(LocalSettings settings) =>
        s_settings = settings ?? throw new ArgumentNullException(nameof(settings));

    /// <summary>Test hook only.</summary>
    internal static void ResetForTests() => s_settings = null;

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
