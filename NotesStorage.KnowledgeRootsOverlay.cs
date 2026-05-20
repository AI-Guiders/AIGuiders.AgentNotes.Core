using System.Text;
using System.Text.RegularExpressions;

namespace AgentNotes.Core;

public sealed partial class NotesStorage
{
    private const string KnowledgeRootsRoutingSectionId = "knowledge-roots-routing-v1";
    private const string KnowledgeRootsIndexRelativePath = "work/local/knowledge-roots-index-v1.md";
    private const int KnowledgeRootPreviewMaxLines = 24;
    private const int MaxRegistryOverlayEntries = 3;

    private static readonly Regex KnowledgeRootIndexLineRegex = new(
        @"^\s*(?<path>[^\s#]+?)\s*=>\s*(?<root>\w+)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly string[] KnowledgeRootsQueryHints =
    [
        "group",
        "public",
        "knowledge",
        "root",
        "roots",
        "chmod",
        "ugo",
        "multi-root",
        "multiroot",
        "knowledge_root",
        "read_only",
        "readonly",
        "registry",
        "org-kb",
        "group-kb",
        "knowledge-roots"
    ];

    private readonly record struct KnowledgeRootRegistryEntry(string RelativePath, string RootId, bool IsPrefix);

    private void AppendKnowledgeRootsOverlayCandidates(
        List<(string id, string content, int score, int matchCount)> candidates,
        IReadOnlyList<string> tokens,
        string query,
        IReadOnlyDictionary<string, string> sections,
        out bool overlayApplied,
        out int registryHits)
    {
        overlayApplied = false;
        registryHits = 0;

        if (!AgentNotesRuntime.IsConfigured)
            return;

        if (AgentNotesRuntime.Settings.ReadOnlyKnowledgeRoots.Count == 0)
            return;

        var queryTouches = QueryTouchesKnowledgeRootsRouting(query, tokens);
        var registry = LoadKnowledgeRootsIndex();
        var registryMatches = new List<(string path, string rootId, int score, bool isPrefix)>();

        foreach (var entry in registry)
        {
            if (!IsConfiguredReadOnlyRoot(entry.RootId))
                continue;

            var score = ScoreKnowledgeRootRegistryEntry(entry, tokens, query);
            if (score <= 0)
                continue;

            registryMatches.Add((entry.RelativePath, entry.RootId, score, entry.IsPrefix));
        }

        registryHits = registryMatches.Count;
        if (!queryTouches && registryMatches.Count == 0)
            return;

        overlayApplied = true;

        if (sections.TryGetValue(KnowledgeRootsRoutingSectionId, out var routingContent))
        {
            var routingScore = registryMatches.Count > 0 ? 52 : queryTouches ? 44 : 36;
            var routingMatches = CountMatches(routingContent, tokens) + (queryTouches ? 2 : 0);
            var existingIndex = candidates.FindIndex(c =>
                string.Equals(c.id, KnowledgeRootsRoutingSectionId, StringComparison.Ordinal));
            if (existingIndex >= 0)
            {
                var cur = candidates[existingIndex];
                if (routingScore > cur.score)
                {
                    candidates[existingIndex] = (
                        cur.id,
                        cur.content,
                        routingScore,
                        Math.Max(cur.matchCount, routingMatches));
                }
            }
            else
            {
                candidates.Add((
                    KnowledgeRootsRoutingSectionId,
                    routingContent,
                    routingScore,
                    Math.Max(1, routingMatches)));
            }
        }

        foreach (var (path, rootId, score, isPrefix) in registryMatches
                     .OrderByDescending(x => x.score)
                     .ThenBy(x => x.path, StringComparer.Ordinal)
                     .Take(MaxRegistryOverlayEntries))
        {
            var id = BuildKnowledgeRootOverlaySectionId(rootId, path, isPrefix);
            if (candidates.Any(c => string.Equals(c.id, id, StringComparison.Ordinal)))
                continue;

            var preview = TryReadKnowledgeRegistryPreview(rootId, path, isPrefix);

            var content = BuildKnowledgeRootRegistryOverlayContent(path, rootId, isPrefix, preview);
            var matchCount = Math.Max(1, CountPathTokenMatches(path, rootId, tokens, isPrefix));
            candidates.Add((id, content, score + 32, matchCount));
        }
    }

    private static bool QueryTouchesKnowledgeRootsRouting(string query, IReadOnlyList<string> tokens)
    {
        foreach (var hint in KnowledgeRootsQueryHints)
        {
            if (query.Contains(hint, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        foreach (var token in tokens)
        {
            foreach (var hint in KnowledgeRootsQueryHints)
            {
                if (token.Contains(hint, StringComparison.OrdinalIgnoreCase)
                    || hint.Contains(token, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }

    private static int ScoreKnowledgeRootRegistryEntry(
        KnowledgeRootRegistryEntry entry,
        IReadOnlyList<string> tokens,
        string query)
    {
        var score = CountMatches(entry.RelativePath, tokens) * 4
                    + CountMatches(entry.RootId, tokens) * 6;

        if (entry.RelativePath.Contains(query, StringComparison.OrdinalIgnoreCase))
            score += 24;
        if (entry.RootId.Contains(query, StringComparison.OrdinalIgnoreCase))
            score += 16;

        foreach (var token in tokens)
        {
            if (entry.RelativePath.Contains(token, StringComparison.OrdinalIgnoreCase))
                score += 4;
            if (string.Equals(entry.RootId, token, StringComparison.OrdinalIgnoreCase))
                score += 8;
        }

        if (entry.IsPrefix)
            score += ScorePrefixRegistryEntry(entry.RelativePath, tokens, query);

        return score;
    }

    private static int ScorePrefixRegistryEntry(
        string prefixPath,
        IReadOnlyList<string> tokens,
        string query)
    {
        var score = 0;
        var segments = prefixPath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var token in tokens)
        {
            if (token.Length < 2)
                continue;

            foreach (var segment in segments)
            {
                if (string.Equals(segment, token, StringComparison.OrdinalIgnoreCase))
                    score += 12;
                else if (segment.Contains(token, StringComparison.OrdinalIgnoreCase))
                    score += 6;
            }

            if (prefixPath.Contains(token, StringComparison.OrdinalIgnoreCase))
                score += 4;
        }

        if (query.Length >= 3 && prefixPath.Contains(query, StringComparison.OrdinalIgnoreCase))
            score += 16;

        return score;
    }

    private static int CountPathTokenMatches(
        string path,
        string rootId,
        IReadOnlyList<string> tokens,
        bool isPrefix)
    {
        var count = 0;
        foreach (var token in tokens)
        {
            if (path.Contains(token, StringComparison.OrdinalIgnoreCase)
                || string.Equals(rootId, token, StringComparison.OrdinalIgnoreCase))
                count++;
        }

        if (isPrefix && count == 0 && tokens.Count > 0)
            count = 1;

        return count;
    }

    private string? TryReadKnowledgeRegistryPreview(string rootId, string path, bool isPrefix)
    {
        if (!isPrefix)
        {
            try
            {
                var exact = ReadKnowledgeFile(null, path, 1, KnowledgeRootPreviewMaxLines, knowledgeRootId: rootId);
                return string.IsNullOrWhiteSpace(exact) ? null : exact;
            }
            catch
            {
                return null;
            }
        }

        var normalized = path.TrimEnd('/');
        foreach (var candidate in new[]
                 {
                     $"{normalized}/README.md",
                     $"{normalized}/scope-contour-map-v1.md",
                 })
        {
            try
            {
                var text = ReadKnowledgeFile(null, candidate, 1, KnowledgeRootPreviewMaxLines, knowledgeRootId: rootId);
                if (!string.IsNullOrWhiteSpace(text))
                    return text;
            }
            catch
            {
                // try next candidate under prefix
            }
        }

        return null;
    }

    private static bool IsConfiguredReadOnlyRoot(string rootId) =>
        AgentNotesRuntime.Settings.ReadOnlyKnowledgeRoots
            .Any(r => string.Equals(r.Id, rootId, StringComparison.OrdinalIgnoreCase));

    private IReadOnlyList<KnowledgeRootRegistryEntry> LoadKnowledgeRootsIndex()
    {
        if (!AgentNotesRuntime.TryGetPrimaryKnowledgeRoot(out var primaryRoot))
            return [];

        var fullPath = Path.Combine(primaryRoot, KnowledgeDirName, KnowledgeRootsIndexRelativePath);
        if (!File.Exists(fullPath))
            return [];

        var lines = File.ReadAllLines(fullPath, Encoding.UTF8);
        var entries = new List<KnowledgeRootRegistryEntry>();
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                continue;

            var match = KnowledgeRootIndexLineRegex.Match(trimmed);
            if (!match.Success)
                continue;

            var path = match.Groups["path"].Value.Trim().Replace('\\', '/');
            var root = match.Groups["root"].Value.Trim();
            if (path.Length == 0 || root.Length == 0)
                continue;
            if (string.Equals(root, "user", StringComparison.OrdinalIgnoreCase))
                continue;

            var isPrefix = path.EndsWith("/", StringComparison.Ordinal);
            if (isPrefix)
                path = path.TrimEnd('/');

            entries.Add(new KnowledgeRootRegistryEntry(path, root, isPrefix));
        }

        return entries;
    }

    private static string BuildKnowledgeRootOverlaySectionId(string rootId, string relativePath, bool isPrefix)
    {
        var safePath = relativePath.Replace('\\', '.').Replace('/', '.');
        var suffix = isPrefix ? ".prefix" : "";
        return $"knowledge-root.{rootId}.{safePath}{suffix}";
    }

    private static string BuildKnowledgeRootRegistryOverlayContent(
        string relativePath,
        string rootId,
        bool isPrefix,
        string? preview)
    {
        var sb = new StringBuilder();
        sb.AppendLine("**Knowledge root overlay** (from `work/local/knowledge-roots-index-v1.md`).");
        sb.AppendLine();
        if (isPrefix)
        {
            sb.AppendLine($"- Prefix: `knowledge/{relativePath}/` (all files under this path)");
        }
        else
        {
            sb.AppendLine($"- Path: `knowledge/{relativePath}`");
        }

        sb.AppendLine($"- Read: `read_knowledge_file` with `knowledge_root_id={rootId}`");
        sb.AppendLine("- Write: primary KB only; this root is read-only.");
        if (!string.IsNullOrWhiteSpace(preview))
        {
            sb.AppendLine();
            sb.AppendLine("Preview:");
            sb.AppendLine("```");
            sb.Append(preview.TrimEnd());
            sb.AppendLine();
            sb.AppendLine("```");
        }

        return sb.ToString().TrimEnd();
    }
}
