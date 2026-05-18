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

    private readonly record struct KnowledgeRootRegistryEntry(string RelativePath, string RootId);

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
        var registryMatches = new List<(string path, string rootId, int score)>();

        foreach (var entry in registry)
        {
            if (!IsConfiguredReadOnlyRoot(entry.RootId))
                continue;

            var score = ScoreKnowledgeRootRegistryEntry(entry, tokens, query);
            if (score <= 0)
                continue;

            registryMatches.Add((entry.RelativePath, entry.RootId, score));
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

        foreach (var (path, rootId, score) in registryMatches
                     .OrderByDescending(x => x.score)
                     .ThenBy(x => x.path, StringComparer.Ordinal)
                     .Take(MaxRegistryOverlayEntries))
        {
            var id = BuildKnowledgeRootOverlaySectionId(rootId, path);
            if (candidates.Any(c => string.Equals(c.id, id, StringComparison.Ordinal)))
                continue;

            string? preview = null;
            try
            {
                preview = ReadKnowledgeFile(null, path, 1, KnowledgeRootPreviewMaxLines, knowledgeRootId: rootId);
                if (string.IsNullOrWhiteSpace(preview))
                    preview = null;
            }
            catch
            {
                // Missing file or unknown root — still emit routing hint without preview.
            }

            var content = BuildKnowledgeRootRegistryOverlayContent(path, rootId, preview);
            var matchCount = Math.Max(1, CountPathTokenMatches(path, rootId, tokens));
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

        return score;
    }

    private static int CountPathTokenMatches(string path, string rootId, IReadOnlyList<string> tokens)
    {
        var count = 0;
        foreach (var token in tokens)
        {
            if (path.Contains(token, StringComparison.OrdinalIgnoreCase)
                || string.Equals(rootId, token, StringComparison.OrdinalIgnoreCase))
                count++;
        }

        return count;
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

            entries.Add(new KnowledgeRootRegistryEntry(path, root));
        }

        return entries;
    }

    private static string BuildKnowledgeRootOverlaySectionId(string rootId, string relativePath)
    {
        var safePath = relativePath.Replace('\\', '.').Replace('/', '.');
        return $"knowledge-root.{rootId}.{safePath}";
    }

    private static string BuildKnowledgeRootRegistryOverlayContent(
        string relativePath,
        string rootId,
        string? preview)
    {
        var sb = new StringBuilder();
        sb.AppendLine("**Knowledge root overlay** (from `work/local/knowledge-roots-index-v1.md`).");
        sb.AppendLine();
        sb.AppendLine($"- Path: `knowledge/{relativePath}`");
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
