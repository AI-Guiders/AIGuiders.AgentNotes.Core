namespace AgentNotes.Core.Configuration;

/// <summary>Корневой документ локального TOML (<c>--config</c>), schema version 1.</summary>
internal sealed class AgentNotesMcpConfigDocument
{
    public int Version { get; set; } = 1;

    public KnowledgeSection? Knowledge { get; set; }

    public WorkspaceSection? Workspace { get; set; }

    public StatusSection? Status { get; set; }

    public LocalSettings Materialize(AgentNotesMcpConfigDocument embeddedDefaults)
    {
        var schemaVersion = Version > 0 ? Version : embeddedDefaults.Version;
        if (schemaVersion != 1)
            throw new InvalidOperationException($"Unsupported config schema version {schemaVersion}; expected 1.");

        var knowledge = Knowledge?.Resolve()
            ?? throw new InvalidOperationException("[knowledge] is required in config.");

        var workspace = WorkspaceSection.Resolve(embeddedDefaults.Workspace, Workspace);

        var status = StatusSection.Resolve(embeddedDefaults.Status, Status);

        return new LocalSettings
        {
            SchemaVersion = schemaVersion,
            PrimaryKnowledgeRoot = knowledge.PrimaryRoot,
            KnowledgeRoots = knowledge.NamedRoots,
            ReadOnlyKnowledgeRoots = knowledge.ReadOnlyRoots,
            Workspace = workspace,
            Status = status
        };
    }
}

internal sealed class KnowledgeSection
{
    public string? Primary { get; set; }

    public Dictionary<string, string>? Roots { get; set; }

    public List<ReadOnlyKnowledgeEntry>? ReadOnly { get; set; }

    public ResolvedKnowledge Resolve()
    {
        if (string.IsNullOrWhiteSpace(Primary))
            throw new InvalidOperationException("[knowledge].primary is required in config.");

        var namedRoots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (Roots is not null)
        {
            foreach (var (key, path) in Roots)
            {
                if (string.IsNullOrWhiteSpace(path))
                    continue;
                namedRoots[key] = Path.GetFullPath(path.Trim());
            }
        }

        var primaryRoot = KnowledgeRootResolver.Resolve(Primary, namedRoots);
        var readOnly = ReadOnlyKnowledgeEntry.ToRuntimeList(ReadOnly);
        foreach (var entry in readOnly)
        {
            if (namedRoots.ContainsKey(entry.Id))
                throw new InvalidOperationException(
                    $"[[knowledge.read_only]] id '{entry.Id}' conflicts with [knowledge.roots].");
        }

        return new ResolvedKnowledge(primaryRoot, namedRoots, readOnly);
    }
}

internal sealed class ReadOnlyKnowledgeEntry
{
    public string? Id { get; set; }

    public string? Path { get; set; }

    internal static IReadOnlyList<ReadOnlyKnowledgeRoot> ToRuntimeList(List<ReadOnlyKnowledgeEntry>? entries)
    {
        if (entries is null || entries.Count == 0)
            return [];

        var list = new List<ReadOnlyKnowledgeRoot>(entries.Count);
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Id) || string.IsNullOrWhiteSpace(entry.Path))
                throw new InvalidOperationException("Each [[knowledge.read_only]] entry requires id and path.");
            list.Add(new ReadOnlyKnowledgeRoot
            {
                Id = entry.Id.Trim(),
                Path = System.IO.Path.GetFullPath(entry.Path.Trim())
            });
        }

        return list;
    }
}

internal sealed class WorkspaceSection
{
    public static WorkspaceSection Example { get; } = new()
    {
        DefaultScope = "example",
        ScopeMap = "example/workspace-scope-map-v1.md",
        ScopeAliases = "example/scope-alias-map-v1.md"
    };

    public string? DefaultScope { get; set; }

    public string? ScopeMap { get; set; }

    public string? ScopeAliases { get; set; }

    /// <summary>Слияние: embedded defaults (нейтральный example) → секция <c>[workspace]</c> в --config.</summary>
    public static WorkspaceSettings Resolve(WorkspaceSection? embeddedDefaults, WorkspaceSection? user)
    {
        var merged = user is null
            ? embeddedDefaults ?? Example
            : (embeddedDefaults ?? Example).Overlay(user);
        return merged.ToRuntimeSettings();
    }

    private WorkspaceSection Overlay(WorkspaceSection higher)
    {
        return new WorkspaceSection
        {
            DefaultScope = higher.DefaultScope ?? DefaultScope,
            ScopeMap = higher.ScopeMap ?? ScopeMap,
            ScopeAliases = higher.ScopeAliases ?? ScopeAliases
        };
    }

    private WorkspaceSettings ToRuntimeSettings() =>
        new()
        {
            DefaultScope = (DefaultScope ?? Example.DefaultScope)!.Trim(),
            ScopeMapRelative = KnowledgeRelativePath.Normalize(ScopeMap ?? Example.ScopeMap!),
            ScopeAliasMapRelative = KnowledgeRelativePath.Normalize(ScopeAliases ?? Example.ScopeAliases!)
        };
}

internal sealed class StatusSection
{
    public bool Enabled { get; set; }

    public int Port { get; set; }

    public string? Bind { get; set; }

    public StatusPreviewSection? Preview { get; set; }

    public static StatusSettings Resolve(StatusSection? embeddedDefaults, StatusSection? user)
    {
        var enabled = user?.Enabled ?? embeddedDefaults?.Enabled ?? false;
        var port = user?.Port > 0 ? user.Port
            : embeddedDefaults?.Port > 0 ? embeddedDefaults.Port
            : 17341;
        var bind = user?.Bind ?? embeddedDefaults?.Bind ?? "127.0.0.1";
        var previewPath = user?.Preview?.Workspace ?? embeddedDefaults?.Preview?.Workspace;
        string? previewWorkspace = string.IsNullOrWhiteSpace(previewPath)
            ? null
            : Path.GetFullPath(previewPath.Trim());

        return new StatusSettings
        {
            Enabled = enabled,
            Port = port,
            Bind = bind.Trim(),
            PreviewWorkspace = previewWorkspace
        };
    }
}

internal sealed class StatusPreviewSection
{
    public string? Workspace { get; set; }
}

internal readonly record struct ResolvedKnowledge(
    string PrimaryRoot,
    IReadOnlyDictionary<string, string> NamedRoots,
    IReadOnlyList<ReadOnlyKnowledgeRoot> ReadOnlyRoots);

internal static class KnowledgeRootResolver
{
    internal static string Resolve(string primarySpec, IReadOnlyDictionary<string, string> namedRoots)
    {
        var spec = primarySpec.Trim();
        if (namedRoots.TryGetValue(spec, out var fromKey))
            return fromKey;
        if (LooksLikeFilesystemPath(spec))
            return Path.GetFullPath(spec);
        throw new InvalidOperationException(
            $"[knowledge].primary = \"{spec}\" is not an absolute path and was not found in [knowledge.roots].");
    }

    private static bool LooksLikeFilesystemPath(string spec) =>
        Path.IsPathRooted(spec)
        || spec.StartsWith("\\\\", StringComparison.Ordinal)
        || (spec.Length >= 2 && spec[1] == ':' && char.IsAsciiLetter(spec[0]));
}

internal static class KnowledgeRelativePath
{
    internal static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("Workspace path under knowledge/ is required.");
        var normalized = path.Replace('\\', '/').TrimStart('/');
        if (normalized.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(normalized))
            throw new InvalidOperationException($"Workspace path must be relative under knowledge/: {path}");
        return normalized;
    }
}
