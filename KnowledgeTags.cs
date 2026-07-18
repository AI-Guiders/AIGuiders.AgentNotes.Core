using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentNotes.Core;

/// <summary>
/// Parse <c>**Tags:** #topic #role</c> lines (playbook-kb-topic-hashtags-v1).
/// </summary>
public static partial class KnowledgeTags
{
    public const string RoleSsot = "ssot";
    public const string RoleResearch = "research";
    public const string RoleLiving = "living";
    public const string RolePlaybook = "playbook";
    public const string RoleAdr = "adr";
    public const string RoleNote = "note";
    public const string RoleTemplate = "template";
    public const string RoleReadme = "readme";
    public const string RoleKb = "kb";
    public const string RoleMap = "map";
    public const string RoleStatus = "status";

    private static readonly HashSet<string> RoleTags = new(StringComparer.OrdinalIgnoreCase)
    {
        RoleSsot, RoleResearch, RoleLiving, RolePlaybook, RoleAdr, RoleNote,
        RoleTemplate, RoleReadme, RoleKb, RoleMap, RoleStatus
    };

    [GeneratedRegex(@"^\*\*Tags:\*\*\s*(?<tags>.+)$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex TagsLineRegex();

    [GeneratedRegex(@"#([A-Za-z][A-Za-z0-9_-]*)", RegexOptions.CultureInvariant)]
    private static partial Regex HashTagRegex();

    public static IReadOnlyList<string> ParseTagsLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];
        var m = TagsLineRegex().Match(text);
        if (!m.Success)
            return [];
        return NormalizeTags(HashTagRegex().Matches(m.Groups["tags"].Value).Select(x => x.Groups[1].Value));
    }

    public static IReadOnlyList<string> ParseAllHashTagsInHead(string? text, int maxChars = 4000)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];
        var head = text.Length <= maxChars ? text : text[..maxChars];
        var fromLine = ParseTagsLine(head);
        if (fromLine.Count > 0)
            return fromLine;
        return NormalizeTags(HashTagRegex().Matches(head).Select(x => x.Groups[1].Value));
    }

    public static bool IsRoleTag(string tag)
    {
        var t = NormalizeOne(tag);
        return t is not null && RoleTags.Contains(t);
    }

    public static IReadOnlyList<string> TopicTags(IEnumerable<string> tags) =>
        tags.Select(NormalizeOne).Where(t => t is not null && !RoleTags.Contains(t!)).Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();

    public static IReadOnlyList<string> RoleTagsOf(IEnumerable<string> tags) =>
        tags.Select(NormalizeOne).Where(t => t is not null && RoleTags.Contains(t!)).Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();

    public static string FormatTagsLine(IEnumerable<string> tags)
    {
        var normalized = NormalizeTags(tags);
        return "**Tags:** " + string.Join(" ", normalized.Select(t => "#" + t));
    }

    public static IReadOnlyList<string> NormalizeTags(IEnumerable<string> raw) =>
        raw.Select(NormalizeOne).Where(t => t is not null).Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();

    public static string? NormalizeOne(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        var t = raw.Trim().TrimStart('#');
        if (t.Length == 0 || !char.IsAsciiLetter(t[0]))
            return null;
        return t.ToLowerInvariant();
    }

    public static string ToJson(object payload) =>
        JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
}
