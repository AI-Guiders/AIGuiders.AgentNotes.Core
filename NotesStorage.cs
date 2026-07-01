using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentNotes.Core;

public sealed partial class NotesStorage
{
    private const string NotesDirName = ".cascade-ide";
    private const string NotesFileName = "agent-notes.md";
    private const string EnvNotesFile = "AGENT_NOTES_FILE";
    private const string RevisionsDirName = ".revisions";
    private const string KnowledgeDirName = "knowledge";

    private readonly object _sync = new();
    private static readonly Regex SectionRegex = new(
        @"<!--\s*section:(?<id>[A-Za-z0-9._-]+)\s*-->\s*(?<content>.*?)\s*<!--\s*/section:\k<id>\s*-->",
        RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex MemoryArchitectureManifestRegex = new(
        @"(?m)^\s*l0_manifest\s*:\s*(?<path>\S+)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Hot notes path: TOML primary root (<c>--config</c>); else <c>AGENT_NOTES_FILE</c>; else <c>workspace_path/.cascade-ide/agent-notes.md</c>.</summary>
    public string GetNotesPath(string workspacePath)
    {
        if (AgentNotesRuntime.TryGetPrimaryKnowledgeRoot(out var primaryRoot))
            return Path.Combine(primaryRoot, NotesFileName);

        var globalPath = Environment.GetEnvironmentVariable(EnvNotesFile);
        if (!string.IsNullOrWhiteSpace(globalPath))
            return Path.GetFullPath(globalPath.Trim());

        var root = Path.GetFullPath(workspacePath.Trim());
        if (File.Exists(root))
            root = Path.GetDirectoryName(root) ?? root;

        return Path.Combine(root, NotesDirName, NotesFileName);
    }

    /// <summary>Resolve knowledge root for reads: <c>knowledge_path</c>, <c>knowledge_root_id</c>, primary from TOML, or legacy inference.</summary>
    public static string ResolveKnowledgeRoot(string? knowledgePath, string? knowledgeRootId = null) =>
        KnowledgeRootResolution.ResolveForRead(knowledgePath, knowledgeRootId);

    /// <summary>Resolve knowledge root for writes (primary only when TOML is loaded).</summary>
    public static string ResolveKnowledgeRootForWrite(string? knowledgePath, string? knowledgeRootId = null) =>
        KnowledgeRootResolution.ResolveForWrite(knowledgePath, knowledgeRootId);

    /// <summary>Legacy fallback when tool omits both <c>knowledge_path</c> and <c>knowledge_root_id</c>.</summary>
    internal static string ResolveKnowledgeRootLegacy()
    {
        if (AgentNotesRuntime.TryGetPrimaryKnowledgeRoot(out var fromSettings))
            return fromSettings;

        var fromEnvNotes = Environment.GetEnvironmentVariable(EnvNotesFile);
        if (!string.IsNullOrWhiteSpace(fromEnvNotes))
        {
            var inferred = TryInferKnowledgeRootFromAgentNotesFilePath(fromEnvNotes.Trim());
            if (inferred is not null)
                return inferred;
        }

        throw new ArgumentException(
            "knowledge_path or knowledge_root_id is required when --config is not loaded and AGENT_NOTES_FILE is unset or does not lie under a directory tree that contains knowledge/.");
    }

    /// <summary>Walks parents from the notes file directory; returns the first directory that contains a <c>knowledge/</c> subfolder (agent-notes repo layout).</summary>
    internal static string? TryInferKnowledgeRootFromAgentNotesFilePath(string agentNotesFilePath)
    {
        var fullPath = Path.GetFullPath(agentNotesFilePath);
        var current = Path.GetDirectoryName(fullPath);
        while (!string.IsNullOrEmpty(current))
        {
            if (Directory.Exists(Path.Combine(current, KnowledgeDirName)))
                return current;

            var parent = Directory.GetParent(current);
            current = parent?.FullName;
        }

        return null;
    }

    /// <summary>Validate relative path under knowledge/: no "..", no leading slash. Returns normalized relative path.</summary>
    private static string ValidateKnowledgeRelativePath(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("file_path is required.");
        if (!TryValidateKnowledgeRelativePath(filePath, out var normalized))
            throw new ArgumentException("file_path must be a relative path under knowledge/ (no '..', no absolute path).");
        return normalized;
    }

    /// <summary>Returns false if <paramref name="filePath"/> is empty, rooted, or contains <c>..</c>.</summary>
    private static bool TryValidateKnowledgeRelativePath(string filePath, out string normalized)
    {
        normalized = filePath.Replace('\\', '/').TrimStart('/');
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(normalized))
        {
            normalized = "";
            return false;
        }

        return true;
    }

    /// <summary>Workspace map paths: TOML <c>[workspace]</c> when <c>--config</c> loaded; else embedded defaults from AgentNotes.Core.</summary>
    private static (string WorkspaceScopeMapRelative, string ScopeAliasMapRelative) ReadWorkspacePathsOrDefaults(string knowledgeRoot)
    {
        if (AgentNotesRuntime.IsConfigured)
        {
            var ws = AgentNotesRuntime.Settings.Workspace;
            return (ws.ScopeMapRelative, ws.ScopeAliasMapRelative);
        }

        _ = knowledgeRoot;
        return McpResolvePathsDefaults.DefaultsPair;
    }

    public string GetKnowledgeFilePath(string? knowledgePath, string filePath, string? knowledgeRootId = null)
    {
        var root = ResolveKnowledgeRoot(knowledgePath, knowledgeRootId);
        return GetKnowledgeFilePathFromRoot(root, filePath);
    }

    private static string GetKnowledgeFilePathFromRoot(string root, string filePath)
    {
        var relative = ValidateKnowledgeRelativePath(filePath);
        return Path.Combine(root, KnowledgeDirName, relative);
    }

    public string ReadKnowledgeFile(string? knowledgePath, string filePath, int? firstLine1Based = null, int? maxLineCount = null, string? knowledgeRootId = null)
    {
        var fullPath = GetKnowledgeFilePath(knowledgePath, filePath, knowledgeRootId);
        if (!File.Exists(fullPath)) return "";
        var full = File.ReadAllText(fullPath, Encoding.UTF8);
        if (firstLine1Based is null && maxLineCount is null) return full;
        return SliceTextByLines(full, firstLine1Based ?? 1, maxLineCount);
    }

    /// <summary>Return a substring of <paramref name="text"/> by line numbers. <paramref name="firstLine1Based"/> is 1-based. <paramref name="maxLineCount"/>: null = to EOF, 0 = empty, N = at most N lines.</summary>
    internal static string SliceTextByLines(string text, int firstLine1Based, int? maxLineCount)
    {
        if (maxLineCount is 0) return "";
        var lines = SplitToLines(text);
        var start = Math.Max(0, firstLine1Based - 1);
        if (start >= lines.Length) return "";
        if (maxLineCount is int cap)
        {
            if (cap < 0) return "";
            var n = Math.Min(cap, lines.Length - start);
            if (n <= 0) return "";
            return string.Join("\n", lines, start, n);
        }
        return string.Join("\n", lines, start, lines.Length - start);
    }

    private static string[] SplitToLines(string text) =>
        text.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);

    public string ListKnowledgeFiles(string? knowledgePath, string? subdir, string? knowledgeRootId = null)
    {
        var root = ResolveKnowledgeRoot(knowledgePath, knowledgeRootId);
        var knowledgeRoot = Path.Combine(root, KnowledgeDirName);
        var searchDir = string.IsNullOrWhiteSpace(subdir)
            ? knowledgeRoot
            : Path.Combine(knowledgeRoot, ValidateKnowledgeRelativePath(subdir.Trim().Replace('\\', '/')));
        if (!Directory.Exists(searchDir))
            return JsonSerializer.Serialize(new { path = searchDir, files = Array.Empty<object>(), total = 0 }, JsonOptions);
        var baseLen = knowledgeRoot.Length;
        var files = Directory.GetFiles(searchDir, "*", SearchOption.AllDirectories)
            .Where(p => !p.Contains(RevisionsDirName, StringComparison.Ordinal))
            .Select(p =>
            {
                var rel = p.Substring(baseLen).TrimStart(Path.DirectorySeparatorChar).Replace('\\', '/');
                var info = new FileInfo(p);
                return new { path = rel, size_bytes = info.Length, modified_utc = info.LastWriteTimeUtc.ToString("O") };
            })
            .OrderBy(x => x.path, StringComparer.Ordinal)
            .ToArray();
        return JsonSerializer.Serialize(new { path = searchDir, files, total = files.Length }, JsonOptions);
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string WriteKnowledgeFile(string? knowledgePath, string filePath, string content, bool saveRevision = true, string? knowledgeRootId = null)
    {
        var root = ResolveKnowledgeRootForWrite(knowledgePath, knowledgeRootId);
        var fullPath = GetKnowledgeFilePathFromRoot(root, filePath);
        if (saveRevision && File.Exists(fullPath))
        {
            var current = File.ReadAllText(fullPath, Encoding.UTF8);
            WriteKnowledgeRevision(root, filePath, current, "write");
        }
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(fullPath, content, Encoding.UTF8);
        return "OK";
    }

    private void WriteKnowledgeRevision(string root, string filePath, string snapshotContent, string reason)
    {
        var revisionsDir = Path.Combine(root, KnowledgeDirName, RevisionsDirName);
        Directory.CreateDirectory(revisionsDir);
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
        var safeName = filePath.Replace('/', '-').Replace('\\', '-');
        var revisionName = $"{timestamp}-{NormalizeReason(reason)}-{safeName}-{ComputeShortHash(snapshotContent)}.md";
        var revisionPath = Path.Combine(revisionsDir, revisionName);
        File.WriteAllText(revisionPath, snapshotContent, Encoding.UTF8);
    }

    public string AppendKnowledgeFile(string? knowledgePath, string filePath, string content, bool saveRevision = true, string? knowledgeRootId = null)
    {
        var root = ResolveKnowledgeRootForWrite(knowledgePath, knowledgeRootId);
        var fullPath = GetKnowledgeFilePathFromRoot(root, filePath);
        var existing = File.Exists(fullPath) ? File.ReadAllText(fullPath, Encoding.UTF8) : "";
        if (saveRevision && existing.Length > 0)
            WriteKnowledgeRevision(root, filePath, existing, "append");
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);
        var separator = existing.Length > 0 && !existing.EndsWith('\n') ? "\n" : "";
        File.WriteAllText(fullPath, existing + separator + content, Encoding.UTF8);
        return "OK";
    }

    public string UpsertKnowledgeSection(string? knowledgePath, string filePath, string sectionId, string content, bool saveRevision = true, string? knowledgeRootId = null)
    {
        var root = ResolveKnowledgeRootForWrite(knowledgePath, knowledgeRootId);
        var fullPath = GetKnowledgeFilePathFromRoot(root, filePath);
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);
        var existing = File.Exists(fullPath) ? File.ReadAllText(fullPath, Encoding.UTF8) : "";
        if (saveRevision && existing.Length > 0)
            WriteKnowledgeRevision(root, filePath, existing, "upsert");
        var startMarker = $"<!-- section:{sectionId} -->";
        var endMarker = $"<!-- /section:{sectionId} -->";
        var sectionBlock = $"{startMarker}\n{content}\n{endMarker}";
        var start = existing.IndexOf(startMarker, StringComparison.Ordinal);
        var end = start >= 0 ? existing.IndexOf(endMarker, start, StringComparison.Ordinal) : -1;
        string next;
        if (start >= 0 && end >= 0)
        {
            var before = existing[..start].TrimEnd('\r', '\n');
            var after = existing[(end + endMarker.Length)..].TrimStart('\r', '\n');
            next = JoinBlocks(before, sectionBlock, after);
        }
        else
        {
            next = JoinBlocks(existing, sectionBlock);
        }
        File.WriteAllText(fullPath, next, Encoding.UTF8);
        return "OK";
    }

    public string DeleteKnowledgeFile(string? knowledgePath, string filePath, string? knowledgeRootId = null)
    {
        var root = ResolveKnowledgeRootForWrite(knowledgePath, knowledgeRootId);
        var fullPath = GetKnowledgeFilePathFromRoot(root, filePath);
        if (!File.Exists(fullPath))
            return "NO_CHANGES";
        File.Delete(fullPath);
        return "OK";
    }

    public string DeleteKnowledgeSection(string? knowledgePath, string filePath, string sectionId, string? knowledgeRootId = null)
    {
        var root = ResolveKnowledgeRootForWrite(knowledgePath, knowledgeRootId);
        var fullPath = GetKnowledgeFilePathFromRoot(root, filePath);
        if (!File.Exists(fullPath))
            return "NO_CHANGES";
        var existing = File.ReadAllText(fullPath, Encoding.UTF8);
        var startMarker = $"<!-- section:{sectionId} -->";
        var endMarker = $"<!-- /section:{sectionId} -->";
        var start = existing.IndexOf(startMarker, StringComparison.Ordinal);
        var end = start >= 0 ? existing.IndexOf(endMarker, start, StringComparison.Ordinal) : -1;
        if (start < 0 || end < 0)
            return "NO_CHANGES";
        var before = existing[..start].TrimEnd('\r', '\n');
        var after = existing[(end + endMarker.Length)..].TrimStart('\r', '\n');
        var next = JoinBlocks(before, after);
        File.WriteAllText(fullPath, next, Encoding.UTF8);
        return "OK";
    }

    public string Read(string workspacePath)
    {
        var filePath = GetNotesPath(workspacePath);
        return File.Exists(filePath) ? File.ReadAllText(filePath, Encoding.UTF8) : "";
    }

    public string Write(string workspacePath, string content) =>
        SaveWithRevision(GetNotesPath(workspacePath), content, "write");

    public string Append(string workspacePath, string contentToAppend)
    {
        var notesPath = GetNotesPath(workspacePath);
        var existing = File.Exists(notesPath) ? File.ReadAllText(notesPath, Encoding.UTF8) : "";
        var separator = existing.Length > 0 && !existing.EndsWith('\n') ? "\n" : "";
        return SaveWithRevision(notesPath, existing + separator + contentToAppend, "append");
    }

    public string UpsertSection(string workspacePath, string sectionId, string content)
    {
        var notesPath = GetNotesPath(workspacePath);
        var existing = File.Exists(notesPath) ? File.ReadAllText(notesPath, Encoding.UTF8) : "";

        var startMarker = $"<!-- section:{sectionId} -->";
        var endMarker = $"<!-- /section:{sectionId} -->";
        var sectionBlock = $"{startMarker}\n{content}\n{endMarker}";

        var start = existing.IndexOf(startMarker, StringComparison.Ordinal);
        var end = start >= 0 ? existing.IndexOf(endMarker, start, StringComparison.Ordinal) : -1;

        string next;
        if (start >= 0 && end >= 0)
        {
            var before = existing[..start].TrimEnd('\r', '\n');
            var after = existing[(end + endMarker.Length)..].TrimStart('\r', '\n');
            next = JoinBlocks(before, sectionBlock, after);
        }
        else
        {
            next = JoinBlocks(existing, sectionBlock);
        }

        return SaveWithRevision(notesPath, next, $"upsert-{sectionId}");
    }

    public string DeleteSection(string workspacePath, string sectionId)
    {
        var notesPath = GetNotesPath(workspacePath);
        if (!File.Exists(notesPath))
            return "NO_CHANGES";
        var existing = File.ReadAllText(notesPath, Encoding.UTF8);
        var startMarker = $"<!-- section:{sectionId} -->";
        var endMarker = $"<!-- /section:{sectionId} -->";
        var start = existing.IndexOf(startMarker, StringComparison.Ordinal);
        var end = start >= 0 ? existing.IndexOf(endMarker, start, StringComparison.Ordinal) : -1;
        if (start < 0 || end < 0)
            return "NO_CHANGES";
        var before = existing[..start].TrimEnd('\r', '\n');
        var after = existing[(end + endMarker.Length)..].TrimStart('\r', '\n');
        var next = JoinBlocks(before, after);
        return SaveWithRevision(notesPath, next, $"delete-{sectionId}");
    }

    public string ListRevisions(string workspacePath, int limit)
    {
        var notesPath = GetNotesPath(workspacePath);
        var revisionsDir = GetRevisionsDir(notesPath);
        if (!Directory.Exists(revisionsDir))
            return "[]";

        var revisions = Directory.GetFiles(revisionsDir, "*.md")
            .OrderByDescending(Path.GetFileName)
            .Take(limit)
            .Select(path =>
            {
                var info = new FileInfo(path);
                return new
                {
                    file = Path.GetFileName(path),
                    size_bytes = info.Length,
                    modified_utc = info.LastWriteTimeUtc.ToString("O")
                };
            })
            .ToArray();

        return JsonSerializer.Serialize(revisions, new JsonSerializerOptions { WriteIndented = true });
    }

    public string Rollback(string workspacePath, string? revisionFile)
    {
        var notesPath = GetNotesPath(workspacePath);
        var revisionsDir = GetRevisionsDir(notesPath);
        if (!Directory.Exists(revisionsDir))
            throw new ArgumentException("No revisions found.");

        var resolvedRevisionFile = revisionFile
            ?? Directory.GetFiles(revisionsDir, "*.md")
                .Select(Path.GetFileName)
                .OrderByDescending(name => name)
                .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(resolvedRevisionFile))
            throw new ArgumentException("No revisions found.");

        var revisionPath = Path.Combine(revisionsDir, resolvedRevisionFile);
        if (!File.Exists(revisionPath))
            throw new ArgumentException("revision_file not found.");

        var target = File.ReadAllText(revisionPath, Encoding.UTF8);
        var result = SaveWithRevision(notesPath, target, $"rollback-{Path.GetFileNameWithoutExtension(resolvedRevisionFile)}");
        return result == "NO_CHANGES" ? $"NO_CHANGES ({resolvedRevisionFile})" : $"OK ({resolvedRevisionFile})";
    }

    public string Search(string workspacePath, string query, int limit)
    {
        var notes = Read(workspacePath);
        var lines = notes.Replace("\r\n", "\n").Split('\n');
        var totalMatches = 0;
        var returned = new List<object>();

        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].Contains(query, StringComparison.OrdinalIgnoreCase))
                continue;

            totalMatches++;
            if (returned.Count >= limit)
                continue;

            returned.Add(new
            {
                line = i + 1,
                text = lines[i]
            });
        }

        var payload = new
        {
            query,
            total_matches = totalMatches,
            returned_matches = returned.Count,
            matches = returned
        };

        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    }

    private string SaveWithRevision(string notesPath, string newContent, string reason)
    {
        lock (_sync)
        {
            var hasCurrent = File.Exists(notesPath);
            var currentContent = hasCurrent ? File.ReadAllText(notesPath, Encoding.UTF8) : "";

            if (currentContent == newContent)
                return "NO_CHANGES";

            if (hasCurrent)
                WriteRevisionSnapshot(notesPath, currentContent, reason);

            AtomicWriteAllText(notesPath, newContent);
            return "OK";
        }
    }

    private static string GetRevisionsDir(string notesPath)
    {
        var dir = Path.GetDirectoryName(notesPath);
        if (string.IsNullOrWhiteSpace(dir))
            throw new ArgumentException("Invalid notes path.");
        return Path.Combine(dir, RevisionsDirName);
    }

    private static void AtomicWriteAllText(string path, string content)
    {
        var dir = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(dir))
            throw new ArgumentException("Invalid target path.");

        Directory.CreateDirectory(dir);
        var tempPath = Path.Combine(dir, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(tempPath, content, Encoding.UTF8);
        File.Move(tempPath, path, true);
    }

    private static void WriteRevisionSnapshot(string notesPath, string snapshotContent, string reason)
    {
        var revisionsDir = GetRevisionsDir(notesPath);
        Directory.CreateDirectory(revisionsDir);

        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
        var revisionName = $"{timestamp}-{NormalizeReason(reason)}-{ComputeShortHash(snapshotContent)}.md";
        var revisionPath = Path.Combine(revisionsDir, revisionName);
        File.WriteAllText(revisionPath, snapshotContent, Encoding.UTF8);
    }

    private static string NormalizeReason(string reason)
    {
        var buffer = new StringBuilder(reason.Length);
        foreach (var ch in reason.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '-')
                buffer.Append(ch);
            else if (buffer.Length == 0 || buffer[^1] != '-')
                buffer.Append('-');
        }

        return buffer.ToString().Trim('-') is { Length: > 0 } normalized
            ? normalized
            : "update";
    }

    private static string ComputeShortHash(string content)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(hash.AsSpan(0, 4)).ToLowerInvariant();
    }

    private static string JoinBlocks(params string[] blocks)
    {
        var nonEmpty = blocks
            .Select(block => block.Trim('\r', '\n'))
            .Where(block => block.Length > 0)
            .ToArray();

        if (nonEmpty.Length == 0)
            return "";

        return string.Join("\n\n", nonEmpty) + "\n";
    }

    private static Dictionary<string, string> ParseSections(string notes)
    {
        var sections = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match match in SectionRegex.Matches(notes))
        {
            var id = match.Groups["id"].Value;
            var content = match.Groups["content"].Value.Trim('\r', '\n');
            sections[id] = content;
        }

        return sections;
    }
}
