namespace AgentNotes.Core;

/// <summary>Resolves knowledge repository roots from TOML (<c>--config</c>), tool args, or legacy inference.</summary>
public static class KnowledgeRootResolution
{
    /// <summary>Resolve root for read operations (<c>read_knowledge_file</c>, <c>list_knowledge_files</c>).</summary>
    public static string ResolveForRead(string? knowledgePath, string? knowledgeRootId) =>
        Resolve(knowledgePath, knowledgeRootId, forWrite: false);

    /// <summary>Resolve root for write operations; only <see cref="LocalSettings.PrimaryKnowledgeRoot"/> is writable when configured.</summary>
    public static string ResolveForWrite(string? knowledgePath, string? knowledgeRootId) =>
        Resolve(knowledgePath, knowledgeRootId, forWrite: true);

    private static string Resolve(string? knowledgePath, string? knowledgeRootId, bool forWrite)
    {
        if (!string.IsNullOrWhiteSpace(knowledgePath) && !string.IsNullOrWhiteSpace(knowledgeRootId))
            throw new ArgumentException("Specify either knowledge_path or knowledge_root_id, not both.");

        if (!string.IsNullOrWhiteSpace(knowledgePath))
        {
            var path = Path.GetFullPath(knowledgePath.Trim());
            if (forWrite)
                EnsureWritable(path);
            return path;
        }

        if (!string.IsNullOrWhiteSpace(knowledgeRootId))
            return ResolveById(knowledgeRootId.Trim(), forWrite);

        var fallback = NotesStorage.ResolveKnowledgeRootLegacy();
        if (forWrite)
            EnsureWritable(fallback);
        return fallback;
    }

    private static string ResolveById(string id, bool forWrite)
    {
        id = id.Trim();

        if (!AgentNotesRuntime.IsConfigured)
            throw new ArgumentException(
                "knowledge_root_id requires --config with [knowledge] and matching [[knowledge.read_only]] or [knowledge.roots].");

        var settings = AgentNotesRuntime.Settings;
        if (settings.KnowledgeRoots.TryGetValue(id, out var named))
        {
            if (forWrite)
                EnsureWritable(named);
            return named;
        }

        var readOnly = settings.ReadOnlyKnowledgeRoots
            .FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));
        if (readOnly is not null)
        {
            if (forWrite)
                throw new InvalidOperationException(
                    $"Knowledge root '{readOnly.Id}' is read-only ({readOnly.Path}). Writes go to primary only.");
            return readOnly.Path;
        }

        throw new ArgumentException(
            $"Unknown knowledge_root_id '{id}'. Known: {FormatKnownRootIds(settings)}.");
    }

    private static void EnsureWritable(string resolvedRoot)
    {
        if (!AgentNotesRuntime.IsConfigured)
            return;

        var primary = AgentNotesRuntime.Settings.PrimaryKnowledgeRoot;
        if (PathsEqual(resolvedRoot, primary))
            return;

        var readOnly = AgentNotesRuntime.Settings.ReadOnlyKnowledgeRoots
            .FirstOrDefault(r => PathsEqual(r.Path, resolvedRoot));
        if (readOnly is not null)
            throw new InvalidOperationException(
                $"Knowledge root '{readOnly.Id}' is read-only. Writes are allowed only on primary ({primary}).");

        throw new InvalidOperationException(
            $"Writes are allowed only on primary knowledge root ({primary}), not '{resolvedRoot}'.");
    }

    internal static bool PathsEqual(string a, string b) =>
        string.Equals(
            Path.GetFullPath(a.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
            Path.GetFullPath(b.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string FormatKnownRootIds(LocalSettings settings)
    {
        var ids = settings.KnowledgeRoots.Keys
            .Concat(settings.ReadOnlyKnowledgeRoots.Select(r => r.Id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static x => x, StringComparer.OrdinalIgnoreCase);
        return string.Join(", ", ids);
    }
}
