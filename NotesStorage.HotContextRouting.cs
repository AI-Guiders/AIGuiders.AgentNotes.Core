using System.Text;
using System.Text.Json;

namespace AgentNotes.Core;

public sealed partial class NotesStorage
{
    public string ReadHotContext(string workspacePath, string? activeScope)
    {
        var notes = Read(workspacePath);
        if (string.IsNullOrWhiteSpace(notes))
            return "";

        var sections = ParseSections(notes);
        var scopeAliases = LoadScopeAliasesMerged();
        var resolvedScope = ResolveScope(activeScope, sections, workspacePath, scopeAliases);

        var notesPath = GetNotesPath(workspacePath);
        var manifest = LoadMemoryArchitectureManifest(sections, notesPath);
        var l0 = ResolveL0Ids(sections, notesPath, manifest);
        var priorityIds = (l0 ?? HotContextDefaults.DefaultL0Ids).ToList();
        var scopeId = ResolveScopeSectionId(resolvedScope, sections, scopeAliases);
        priorityIds.Add(scopeId);

        var loaded = new List<string>();
        var blocks = new List<string>();
        foreach (var id in priorityIds.Distinct(StringComparer.Ordinal))
        {
            if (IsHotExcluded(id, manifest?.HotContextSectionExclusions))
                continue;
            if (!sections.TryGetValue(id, out var content))
                continue;

            loaded.Add(id);
            blocks.Add($"<!-- section:{id} -->\n{content}\n<!-- /section:{id} -->");
        }

        var payload = new
        {
            active_scope = resolvedScope,
            loaded_sections = loaded,
            content = JoinBlocks(blocks.ToArray()).TrimEnd('\n')
        };

        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    }

    public string MemoryHealth(string workspacePath, string? activeScope)
    {
        var notesPath = GetNotesPath(workspacePath);
        var notes = Read(workspacePath);
        var sections = ParseSections(notes);
        var scopeAliases = LoadScopeAliasesMerged();
        var resolvedScope = ResolveScope(activeScope, sections, workspacePath, scopeAliases);
        var hotSectionIds = BuildHotSectionIds(resolvedScope, sections, notesPath, scopeAliases);
        var hotSections = hotSectionIds
            .Where(sections.ContainsKey)
            .Select(id => new
            {
                id,
                chars = sections[id].Length,
                lines = CountLines(sections[id])
            })
            .ToArray();

        var hotChars = hotSections.Sum(x => x.chars);
        var hotLines = hotSections.Sum(x => x.lines);
        var manifestForHealth = LoadMemoryArchitectureManifest(sections, notesPath);
        var (warnBudget, critBudget) = ResolveHotBudgetChars(manifestForHealth);

        var missingCoreSections = HotContextDefaults.RequiredCoreSectionIds
            .Where(required => hotSectionIds.Contains(required, StringComparer.Ordinal))
            .Where(required => !sections.ContainsKey(required))
            .ToArray();

        var warnings = new List<string>();
        var recommendCompaction = false;
        warnings.AddRange(ValidateMemoryArchitecture(sections, notesPath));

        if (hotChars > critBudget)
        {
            warnings.Add("hot_context_over_critical_budget");
            recommendCompaction = true;
        }
        else if (hotChars > warnBudget)
        {
            warnings.Add("hot_context_over_warning_budget");
            recommendCompaction = true;
        }

        if (missingCoreSections.Length > 0)
            warnings.Add("missing_core_sections");

        var healthLevel = warnings.Contains("hot_context_over_critical_budget", StringComparer.Ordinal)
            ? "critical"
            : warnings.Count > 0
                ? "warning"
                : "good";

        var recommendations = new List<string>();
        if (recommendCompaction)
            recommendations.Add("Run compact_hot_context with apply=true after preview to keep L0/L1 small.");
        if (missingCoreSections.Length > 0)
            recommendations.Add("Restore required core sections via upsert_agent_notes_section.");
        if (recommendations.Count == 0)
            recommendations.Add("Keep current memory shape; no immediate action required.");

        var payload = new
        {
            workspace_path = workspacePath,
            notes_path = notesPath,
            notes_exists = File.Exists(notesPath),
            resolved_scope = resolvedScope,
            total_chars = notes.Length,
            total_lines = CountLines(notes),
            section_count = sections.Count,
            hot_context = new
            {
                section_ids = hotSectionIds,
                loaded_section_count = hotSections.Length,
                chars = hotChars,
                lines = hotLines
            },
            missing_core_sections = missingCoreSections,
            largest_sections = sections
                .Select(kv => new
                {
                    id = kv.Key,
                    chars = kv.Value.Length,
                    lines = CountLines(kv.Value)
                })
                .OrderByDescending(x => x.chars)
                .Take(5)
                .ToArray(),
            warnings,
            recommend_compaction = recommendCompaction,
            health_level = healthLevel,
            recommendations
        };

        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    }

    public string RouteContext(
        string workspacePath,
        string query,
        string? activeScope,
        int maxSections,
        int maxChars)
    {
        var notes = Read(workspacePath);
        if (string.IsNullOrWhiteSpace(notes))
            return JsonSerializer.Serialize(new
            {
                query,
                selected = Array.Empty<object>(),
                assembled_context = ""
            }, new JsonSerializerOptions { WriteIndented = true });

        var sections = ParseSections(notes);
        var scopeAliases = LoadScopeAliasesMerged();
        var resolvedScope = ResolveScope(activeScope, sections, workspacePath, scopeAliases);
        var notesPath = GetNotesPath(workspacePath);
        var hotSectionIds = BuildHotSectionIds(resolvedScope, sections, notesPath, scopeAliases);
        var boosted = hotSectionIds
            .Select((id, idx) => (id, bonus: Math.Max(0, 30 - idx * 2)))
            .ToDictionary(x => x.id, x => x.bonus, StringComparer.Ordinal);

        var tokens = TokenizeQuery(query);
        var candidates = new List<(string id, string content, int score, int matchCount)>();
        foreach (var (id, content) in sections)
        {
            var matchCount = CountMatches(content, tokens) + CountMatches(id, tokens);
            var score = matchCount * 4;

            if (content.Contains(query, StringComparison.OrdinalIgnoreCase))
                score += 24;
            if (id.Contains(query, StringComparison.OrdinalIgnoreCase))
                score += 20;
            if (boosted.TryGetValue(id, out var bonus))
                score += bonus;

            if (score <= 0)
                continue;

            candidates.Add((id, content, score, matchCount));
        }

        var selected = candidates
            .OrderByDescending(x => x.score)
            .ThenBy(x => x.id, StringComparer.Ordinal)
            .Take(maxSections)
            .ToArray();

        var assembled = new StringBuilder();
        var emitted = new List<object>();
        var truncated = false;
        foreach (var item in selected)
        {
            var block = $"<!-- section:{item.id} -->\n{item.content}\n<!-- /section:{item.id} -->\n\n";
            if (assembled.Length + block.Length > maxChars)
            {
                truncated = true;
                break;
            }

            assembled.Append(block);
            emitted.Add(new
            {
                id = item.id,
                score = item.score,
                match_count = item.matchCount,
                chars = item.content.Length,
                lines = CountLines(item.content),
                preview = BuildPreview(item.content, 220)
            });
        }

        var payload = new
        {
            query,
            resolved_scope = resolvedScope,
            total_candidates = candidates.Count,
            selected_count = emitted.Count,
            max_sections = maxSections,
            max_chars = maxChars,
            truncated,
            selected = emitted,
            assembled_context = assembled.ToString().TrimEnd('\n')
        };

        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    }

    public string ExtractFromArchive(string workspacePath, string query, string? revisionFile, int limit, int contextLines)
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

        var text = File.ReadAllText(revisionPath, Encoding.UTF8);
        var lines = text.Replace("\r\n", "\n").Split('\n');

        var totalMatches = 0;
        var matches = new List<object>();
        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].Contains(query, StringComparison.OrdinalIgnoreCase))
                continue;

            totalMatches++;
            if (matches.Count >= limit)
                continue;

            var start = Math.Max(0, i - contextLines);
            var end = Math.Min(lines.Length - 1, i + contextLines);
            var window = new List<object>();
            for (var j = start; j <= end; j++)
            {
                window.Add(new
                {
                    line = j + 1,
                    text = lines[j]
                });
            }

            matches.Add(new
            {
                line = i + 1,
                text = lines[i],
                context = window
            });
        }

        var payload = new
        {
            revision_file = resolvedRevisionFile,
            query,
            total_matches = totalMatches,
            returned_matches = matches.Count,
            matches
        };

        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    }

    public string CompactHotContext(string workspacePath, bool apply)
    {
        var notesPath = GetNotesPath(workspacePath);
        var existing = File.Exists(notesPath) ? File.ReadAllText(notesPath, Encoding.UTF8) : "";
        var compacted = CompactNotes(existing, notesPath);

        if (!apply)
        {
            var payload = new
            {
                changed = !string.Equals(existing, compacted, StringComparison.Ordinal),
                content = compacted.TrimEnd('\n')
            };

            return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        }

        return SaveWithRevision(notesPath, compacted, "compact-hot-context");
    }
}
