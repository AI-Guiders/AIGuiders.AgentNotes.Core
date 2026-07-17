using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AgentNotes.Core;

/// <summary>
/// <!-- section:id --> … <!-- /section:id --> integrity helpers.
/// Strict upsert rejects duplicates / unclosed / orphan closes instead of appending and bloating the file.
/// </summary>
public static partial class SectionMarkup
{
    [GeneratedRegex(@"<!--\s*section:(?<id>[A-Za-z0-9._-]+)\s*-->", RegexOptions.CultureInvariant)]
    private static partial Regex OpenMarkerRegex();

    [GeneratedRegex(@"<!--\s*/section:(?<id>[A-Za-z0-9._-]+)\s*-->", RegexOptions.CultureInvariant)]
    private static partial Regex CloseMarkerRegex();

    [GeneratedRegex(
        @"<!--\s*section:(?<id>[A-Za-z0-9._-]+)\s*-->\s*(?<content>.*?)\s*<!--\s*/section:\k<id>\s*-->",
        RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex CompleteBlockRegex();

    /// <summary>Null when markup is well-formed (at most one complete block per id; no orphans/unclosed).</summary>
    public static string? DescribeProblems(string text)
    {
        var report = Analyze(text);
        return report.Ok ? null : report.Summary;
    }

    public static SectionValidationReport Analyze(string text)
    {
        text ??= "";
        var openIds = OpenMarkerRegex().Matches(text).Select(m => m.Groups["id"].Value).ToArray();
        var closeIds = CloseMarkerRegex().Matches(text).Select(m => m.Groups["id"].Value).ToArray();
        var complete = CompleteBlockRegex().Matches(text);
        var completeIds = complete.Select(m => m.Groups["id"].Value).ToArray();

        var sectionIds = completeIds.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var duplicates = completeIds
            .GroupBy(x => x, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => new SectionDuplicate(g.Key, g.Count()))
            .OrderBy(x => x.Id, StringComparer.Ordinal)
            .ToArray();

        var problems = new List<string>();
        var events = new List<(int Index, bool IsOpen, string Id)>();
        foreach (Match m in OpenMarkerRegex().Matches(text))
            events.Add((m.Index, true, m.Groups["id"].Value));
        foreach (Match m in CloseMarkerRegex().Matches(text))
            events.Add((m.Index, false, m.Groups["id"].Value));
        events.Sort((a, b) => a.Index.CompareTo(b.Index));

        var stack = new Stack<string>();
        var completed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (index, isOpen, id) in events)
        {
            if (isOpen)
            {
                if (completed.Contains(id) || stack.Contains(id))
                    problems.Add($"duplicate section '{id}'");
                stack.Push(id);
            }
            else
            {
                if (stack.Count == 0)
                {
                    problems.Add($"orphan close '{id}' at index {index}");
                    continue;
                }

                var top = stack.Pop();
                if (!string.Equals(top, id, StringComparison.Ordinal))
                    problems.Add($"close mismatch (open '{top}', close '{id}') at index {index}");
                else
                    completed.Add(id);
            }
        }

        if (stack.Count > 0)
            problems.Add("unclosed: " + string.Join(", ", stack.Reverse()));

        problems = problems.Distinct(StringComparer.Ordinal).ToList();
        var ok = problems.Count == 0 && duplicates.Length == 0;
        var summary = ok
            ? null
            : "REJECTED: " + string.Join("; ", problems.Count > 0 ? problems : duplicates.Select(d => $"duplicate '{d.Id}' x{d.Count}"));

        return new SectionValidationReport(
            Ok: ok,
            Summary: summary,
            SectionIds: sectionIds,
            OpenMarkerCount: openIds.Length,
            CloseMarkerCount: closeIds.Length,
            CompleteBlockCount: completeIds.Length,
            Duplicates: duplicates,
            Problems: problems.ToArray());
    }

    public static void ThrowIfInvalid(string text)
    {
        var problem = DescribeProblems(text);
        if (problem is not null)
            throw new InvalidOperationException(problem);
    }

    /// <summary>
    /// Collapse duplicate complete blocks (keep last), drop orphan/unclosed marker debris,
    /// emit canonical <!-- section:id --> blocks. Preserves non-section preamble text.
    /// </summary>
    public static string Normalize(string text)
    {
        text ??= "";
        var matches = CompleteBlockRegex().Matches(text);
        var order = new List<string>();
        var contents = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in matches)
        {
            var id = m.Groups["id"].Value;
            var content = m.Groups["content"].Value.Trim('\r', '\n');
            if (!contents.ContainsKey(id))
                order.Add(id);
            contents[id] = content;
        }

        var remainder = text;
        // Remove from end so indices stay valid.
        for (var i = matches.Count - 1; i >= 0; i--)
        {
            var m = matches[i];
            remainder = remainder.Remove(m.Index, m.Length);
        }

        remainder = OpenMarkerRegex().Replace(remainder, "");
        remainder = CloseMarkerRegex().Replace(remainder, "");
        remainder = Regex.Replace(remainder.Replace("\r\n", "\n"), @"\n{3,}", "\n\n").Trim('\r', '\n');

        var blocks = new List<string>();
        if (remainder.Length > 0)
            blocks.Add(remainder);
        foreach (var id in order)
            blocks.Add($"<!-- section:{id} -->\n{contents[id]}\n<!-- /section:{id} -->");

        return JoinBlocks(blocks.ToArray());
    }

    /// <summary>Replace the single well-formed block for <paramref name="sectionId"/>, or append if absent.</summary>
    public static string UpsertBlock(string existing, string sectionId, string content)
    {
        ThrowIfInvalid(existing);

        var startMarker = $"<!-- section:{sectionId} -->";
        var endMarker = $"<!-- /section:{sectionId} -->";
        var sectionBlock = $"{startMarker}\n{content}\n{endMarker}";

        var blockPattern = $@"<!--\s*section:{Regex.Escape(sectionId)}\s*-->\s.*?<!--\s*/section:{Regex.Escape(sectionId)}\s*-->";
        var blockRegex = new Regex(blockPattern, RegexOptions.Singleline | RegexOptions.CultureInvariant);
        var matches = blockRegex.Matches(existing);
        if (matches.Count > 1)
            throw new InvalidOperationException(
                $"REJECTED: duplicate section '{sectionId}' ({matches.Count} blocks).");

        if (matches.Count == 1)
        {
            var m = matches[0];
            var before = existing[..m.Index].TrimEnd('\r', '\n');
            var after = existing[(m.Index + m.Length)..].TrimStart('\r', '\n');
            return JoinBlocks(before, sectionBlock, after);
        }

        return JoinBlocks(existing, sectionBlock);
    }

    public static string ToJson(SectionValidationReport report) =>
        JsonSerializer.Serialize(report, JsonOptions);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

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
}

public sealed record SectionDuplicate(string Id, int Count);

public sealed record SectionValidationReport(
    bool Ok,
    string? Summary,
    string[] SectionIds,
    int OpenMarkerCount,
    int CloseMarkerCount,
    int CompleteBlockCount,
    SectionDuplicate[] Duplicates,
    string[] Problems);
