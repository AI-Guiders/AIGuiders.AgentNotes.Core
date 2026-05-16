namespace AgentNotes.Core;

/// <summary>Effective settings after merging embedded defaults with user TOML (<c>--config</c>).</summary>
public sealed class LocalSettings
{
    public required int SchemaVersion { get; init; }

    /// <summary>Absolute path to primary knowledge repository root (directory containing <c>knowledge/</c>).</summary>
    public required string PrimaryKnowledgeRoot { get; init; }

    public required IReadOnlyDictionary<string, string> KnowledgeRoots { get; init; }

    public required IReadOnlyList<ReadOnlyKnowledgeRoot> ReadOnlyKnowledgeRoots { get; init; }

    public required WorkspaceSettings Workspace { get; init; }

    public required StatusSettings Status { get; init; }
}

public sealed class WorkspaceSettings
{
    public required string DefaultScope { get; init; }

    /// <summary>Path relative to <c>knowledge/</c> under primary root.</summary>
    public required string ScopeMapRelative { get; init; }

    public required string ScopeAliasMapRelative { get; init; }
}

public sealed class ReadOnlyKnowledgeRoot
{
    public required string Id { get; init; }

    public required string Path { get; init; }
}

public sealed class StatusSettings
{
    public required bool Enabled { get; init; }

    public required int Port { get; init; }

    public required string Bind { get; init; }

    public string? PreviewWorkspace { get; init; }
}
