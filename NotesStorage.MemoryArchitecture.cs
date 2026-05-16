using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentNotes.Core;

public sealed partial class NotesStorage
{
    private const string DefaultMemoryArchitectureManifestRelativePath = "knowledge/META/memory-architecture-v1.json";

    /// <summary>Parse L0 section IDs from memory-architecture-v1 content (block after "### L0:" until next "###").</summary>
    private static IReadOnlyList<string>? ParseL0FromMemoryArchitecture(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return null;

        var lines = content.Replace("\r\n", "\n").Split('\n');
        var inL0 = false;
        var ids = new List<string>();
        foreach (var line in lines)
        {
            var t = line.Trim();
            if (t.StartsWith("### L0:", StringComparison.OrdinalIgnoreCase))
            {
                inL0 = true;
                continue;
            }
            if (inL0)
            {
                if (t.StartsWith("### ", StringComparison.Ordinal))
                    break;
                if (t.StartsWith("- ", StringComparison.Ordinal))
                {
                    var rest = t[2..].Trim();
                    var id = rest.Split([' ', '(', '\t'], 2, StringSplitOptions.None)[0].Trim();
                    if (id.Length > 0 && Regex.IsMatch(id, "^[A-Za-z0-9._-]+$"))
                        ids.Add(id);
                }
            }
        }

        return ids.Count > 0 ? ids : null;
    }

    private static string ResolveCanonRootFromNotesPath(string notesPath)
    {
        var dir = Path.GetDirectoryName(notesPath);
        if (string.IsNullOrWhiteSpace(dir))
            throw new ArgumentException("Invalid notes path.");
        return dir;
    }

    private static string? TryParseManifestRelativePath(string? memoryArchitectureContent)
    {
        if (string.IsNullOrWhiteSpace(memoryArchitectureContent))
            return null;
        var match = MemoryArchitectureManifestRegex.Match(memoryArchitectureContent);
        return match.Success ? match.Groups["path"].Value.Trim() : null;
    }

    private static string? TryResolveManifestFullPath(string notesPath, string? manifestPath)
    {
        if (string.IsNullOrWhiteSpace(manifestPath))
            return null;
        var p = manifestPath.Trim().Trim('"');
        if (p.Length == 0)
            return null;

        var canonRoot = ResolveCanonRootFromNotesPath(notesPath);

        if (p.StartsWith("knowledge/", StringComparison.OrdinalIgnoreCase) || p.StartsWith("knowledge\\", StringComparison.OrdinalIgnoreCase))
            return Path.GetFullPath(Path.Combine(canonRoot, p));

        if (p.StartsWith("./", StringComparison.Ordinal) || p.StartsWith(".\\", StringComparison.Ordinal))
            return Path.GetFullPath(Path.Combine(canonRoot, p));

        return Path.IsPathRooted(p) ? Path.GetFullPath(p) : Path.GetFullPath(Path.Combine(canonRoot, "knowledge", p));
    }

    private static MemoryArchitectureManifestData? TryLoadMemoryArchitectureManifest(string notesPath, string manifestPath)
    {
        var fullPath = TryResolveManifestFullPath(notesPath, manifestPath);
        if (string.IsNullOrWhiteSpace(fullPath) || !File.Exists(fullPath))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(fullPath, Encoding.UTF8));
            var root = doc.RootElement;

            var l0 = new List<string>();
            if (root.TryGetProperty("l0", out var l0El) && l0El.ValueKind == JsonValueKind.Array)
                AppendManifestIds(l0, l0El);

            if (root.TryGetProperty("l0_owner", out var l0OwnerEl) && l0OwnerEl.ValueKind == JsonValueKind.Array)
                AppendManifestIds(l0, l0OwnerEl);

            IReadOnlyList<string>? suffix = null;
            if (root.TryGetProperty("compact_order_suffix", out var suffixEl) && suffixEl.ValueKind == JsonValueKind.Array)
            {
                var list = new List<string>();
                foreach (var item in suffixEl.EnumerateArray())
                {
                    var id = item.ValueKind == JsonValueKind.String ? (item.GetString() ?? "").Trim() : "";
                    if (id.Length == 0)
                        continue;
                    if (Regex.IsMatch(id, "^[A-Za-z0-9._-]+$"))
                        list.Add(id);
                }
                suffix = list.Count > 0 ? list : null;
            }

            int? warnBudget = null;
            int? critBudget = null;
            if (root.TryGetProperty("hot_context_budget_warning_chars", out var wEl) && wEl.ValueKind == JsonValueKind.Number)
                warnBudget = wEl.GetInt32();
            if (root.TryGetProperty("hot_context_budget_critical_chars", out var cEl) && cEl.ValueKind == JsonValueKind.Number)
                critBudget = cEl.GetInt32();

            IReadOnlyList<string>? exclusions = null;
            if (root.TryGetProperty("hot_context_section_exclusions", out var exEl) && exEl.ValueKind == JsonValueKind.Array)
            {
                var list = new List<string>();
                foreach (var item in exEl.EnumerateArray())
                {
                    var id = item.ValueKind == JsonValueKind.String ? (item.GetString() ?? "").Trim() : "";
                    if (id.Length == 0)
                        continue;
                    if (Regex.IsMatch(id, "^[A-Za-z0-9._-]+$"))
                        list.Add(id);
                }
                exclusions = list.Count > 0 ? list : null;
            }

            return new MemoryArchitectureManifestData(l0, suffix, warnBudget, critBudget, exclusions);
        }
        catch
        {
            return null;
        }
    }

    private static void AppendManifestIds(List<string> target, JsonElement array)
    {
        foreach (var item in array.EnumerateArray())
        {
            var id = item.ValueKind == JsonValueKind.String ? (item.GetString() ?? "").Trim() : "";
            if (id.Length == 0)
                continue;
            if (Regex.IsMatch(id, "^[A-Za-z0-9._-]+$"))
                target.Add(id);
        }
    }

    private static MemoryArchitectureManifestData? LoadMemoryArchitectureManifest(IReadOnlyDictionary<string, string> sections, string notesPath)
    {
        var memoryArch = sections.GetValueOrDefault("memory-architecture-v1");
        var manifestPath = TryParseManifestRelativePath(memoryArch);
        if (string.IsNullOrWhiteSpace(manifestPath))
            manifestPath = DefaultMemoryArchitectureManifestRelativePath;
        return TryLoadMemoryArchitectureManifest(notesPath, manifestPath);
    }

    private static (int Warning, int Critical) ResolveHotBudgetChars(MemoryArchitectureManifestData? manifest)
    {
        var w = manifest?.HotBudgetWarningChars ?? HotContextDefaults.HotContextBudgetWarningChars;
        var c = manifest?.HotBudgetCriticalChars ?? HotContextDefaults.HotContextBudgetCriticalChars;
        if (w < 1)
            w = HotContextDefaults.HotContextBudgetWarningChars;
        if (c < 1)
            c = HotContextDefaults.HotContextBudgetCriticalChars;
        if (w >= c)
        {
            return (HotContextDefaults.HotContextBudgetWarningChars, HotContextDefaults.HotContextBudgetCriticalChars);
        }

        return (w, c);
    }

    private static bool IsHotExcluded(string sectionId, IReadOnlyList<string>? manifestExclusions)
    {
        if (HotContextDefaults.IsBuiltInHotExclusion(sectionId))
            return true;
        if (manifestExclusions is null)
            return false;
        foreach (var x in manifestExclusions)
        {
            if (sectionId.Equals(x, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static IReadOnlyList<string>? ResolveL0Ids(IReadOnlyDictionary<string, string> sections, string notesPath, MemoryArchitectureManifestData? manifest = null)
    {
        manifest ??= LoadMemoryArchitectureManifest(sections, notesPath);
        if (manifest is { L0.Count: > 0 })
            return manifest.L0;

        var memoryArch = sections.GetValueOrDefault("memory-architecture-v1");
        return ParseL0FromMemoryArchitecture(memoryArch);
    }

    private static IReadOnlyList<string>? ResolveCompactOrderSuffix(IReadOnlyDictionary<string, string> sections, string notesPath)
    {
        return LoadMemoryArchitectureManifest(sections, notesPath)?.CompactOrderSuffix;
    }

    private static string[] BuildHotSectionIds(string resolvedScope, IReadOnlyDictionary<string, string> sections, string notesPath, IReadOnlyDictionary<string, string> scopeAliases)
    {
        var manifest = LoadMemoryArchitectureManifest(sections, notesPath);
        var l0 = ResolveL0Ids(sections, notesPath, manifest);
        var ids = (l0 ?? HotContextDefaults.DefaultL0Ids).ToList();
        ids.Add(ResolveScopeSectionId(resolvedScope, sections, scopeAliases));
        return ids.Where(id => !IsHotExcluded(id, manifest?.HotContextSectionExclusions)).Distinct(StringComparer.Ordinal).ToArray();
    }

    private static int CountLines(string content)
    {
        if (string.IsNullOrEmpty(content))
            return 0;
        return content.Replace("\r\n", "\n").Split('\n').Length;
    }

    private static string[] TokenizeQuery(string query)
    {
        var tokens = Regex.Split(query.ToLowerInvariant(), @"[^a-zа-я0-9._-]+")
            .Where(token => token.Length >= 3)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return tokens.Length > 0 ? tokens : [query.ToLowerInvariant()];
    }

    private static int CountMatches(string text, IReadOnlyList<string> tokens)
    {
        var normalized = text.ToLowerInvariant();
        var count = 0;
        foreach (var token in tokens)
        {
            if (normalized.Contains(token, StringComparison.Ordinal))
                count++;
        }

        return count;
    }

    private static string BuildPreview(string content, int maxChars)
    {
        var normalized = Regex.Replace(content.Replace("\r\n", "\n"), @"\s+", " ").Trim();
        if (normalized.Length <= maxChars)
            return normalized;
        return normalized[..maxChars] + "...";
    }

    private static string CompactNotes(string notes, string notesPath)
    {
        var sections = ParseSections(notes);
        if (sections.Count == 0)
            return NormalizeWhitespace(notes);

        var l0 = ResolveL0Ids(sections, notesPath);
        var startIds = (l0 ?? HotContextDefaults.DefaultL0Ids).ToList();
        var manifestSuffix = ResolveCompactOrderSuffix(sections, notesPath);
        var suffixSeed = manifestSuffix?.ToArray() ?? HotContextDefaults.DefaultCompactOrderSuffix;
        var suffixIds = suffixSeed.Where(id => !startIds.Contains(id, StringComparer.Ordinal));
        var preferredOrder = startIds.Concat(suffixIds).ToArray();

        var blocks = new List<string>();
        foreach (var id in preferredOrder)
        {
            if (!sections.TryGetValue(id, out var content))
                continue;

            blocks.Add($"<!-- section:{id} -->\n{content}\n<!-- /section:{id} -->");
            sections.Remove(id);
        }

        foreach (var id in sections.Keys.OrderBy(x => x, StringComparer.Ordinal))
        {
            blocks.Add($"<!-- section:{id} -->\n{sections[id]}\n<!-- /section:{id} -->");
        }

        return JoinBlocks(blocks.ToArray());
    }

    private static IReadOnlyList<string> ValidateMemoryArchitecture(IReadOnlyDictionary<string, string> sections, string notesPath)
    {
        var warnings = new List<string>();
        var memoryArch = sections.GetValueOrDefault("memory-architecture-v1");
        var manifestRel = TryParseManifestRelativePath(memoryArch);
        if (string.IsNullOrWhiteSpace(manifestRel))
            return warnings;

        var fullPath = TryResolveManifestFullPath(notesPath, manifestRel);
        if (string.IsNullOrWhiteSpace(fullPath) || !File.Exists(fullPath))
        {
            warnings.Add("memory_arch_manifest_missing");
            return warnings;
        }

        var manifest = TryLoadMemoryArchitectureManifest(notesPath, manifestRel);
        if (manifest is null)
        {
            warnings.Add("memory_arch_manifest_invalid_json");
            return warnings;
        }

        var ids = manifest.L0;
        if (ids.Count == 0)
        {
            warnings.Add("memory_arch_manifest_l0_empty");
            return warnings;
        }

        var missing = ids.Where(id => !sections.ContainsKey(id)).Take(8).ToArray();
        if (missing.Length > 0)
            warnings.Add("memory_arch_manifest_missing_sections:" + string.Join(",", missing));

        return warnings;
    }

    private static string NormalizeWhitespace(string text)
    {
        var normalized = text.Replace("\r\n", "\n");
        normalized = Regex.Replace(normalized, @"\n{3,}", "\n\n");
        return normalized.EndsWith('\n') ? normalized : normalized + "\n";
    }
}
