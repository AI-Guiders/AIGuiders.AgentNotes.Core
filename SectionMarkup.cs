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

    /// <summary>Null when markup is well-formed (at most one complete block per id; no orphans/unclosed).</summary>
    public static string? DescribeProblems(string text)
    {
        if (string.IsNullOrEmpty(text))
            return null;

        var events = new List<(int Index, bool IsOpen, string Id)>();
        foreach (Match m in OpenMarkerRegex().Matches(text))
            events.Add((m.Index, true, m.Groups["id"].Value));
        foreach (Match m in CloseMarkerRegex().Matches(text))
            events.Add((m.Index, false, m.Groups["id"].Value));

        events.Sort((a, b) => a.Index.CompareTo(b.Index));

        var stack = new Stack<string>();
        var completed = new HashSet<string>(StringComparer.Ordinal);
        var openCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var closeCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var (index, isOpen, id) in events)
        {
            if (isOpen)
            {
                openCounts[id] = openCounts.GetValueOrDefault(id) + 1;
                if (completed.Contains(id) || stack.Contains(id))
                    return $"REJECTED: duplicate section '{id}' (broken <!-- section --> markup). Run normalize/validate; refuse to upsert and bloat the file.";
                stack.Push(id);
            }
            else
            {
                closeCounts[id] = closeCounts.GetValueOrDefault(id) + 1;
                if (stack.Count == 0)
                    return $"REJECTED: orphan close <!-- /section:{id} --> at index {index}.";
                var top = stack.Pop();
                if (!string.Equals(top, id, StringComparison.Ordinal))
                    return $"REJECTED: section close mismatch (open '{top}', close '{id}') at index {index}.";
                completed.Add(id);
            }
        }

        if (stack.Count > 0)
            return $"REJECTED: unclosed section(s): {string.Join(", ", stack.Reverse())}.";

        foreach (var (id, opens) in openCounts)
        {
            var closes = closeCounts.GetValueOrDefault(id);
            if (opens != closes)
                return $"REJECTED: section '{id}' open/close count mismatch (open={opens}, close={closes}).";
            if (opens > 1)
                return $"REJECTED: duplicate section '{id}' ({opens} blocks).";
        }

        return null;
    }

    public static void ThrowIfInvalid(string text)
    {
        var problem = DescribeProblems(text);
        if (problem is not null)
            throw new InvalidOperationException(problem);
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

        // No complete block: any leftover open/close for this id already rejected by ThrowIfInvalid.
        return JoinBlocks(existing, sectionBlock);
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
}
