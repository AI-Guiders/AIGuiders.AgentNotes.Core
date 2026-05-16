using System.Text;
using System.Text.RegularExpressions;

namespace AgentNotes.Core;

public sealed partial class NotesStorage
{
    /// <summary>Reads scope alias file under canon (path from <c>knowledge/META/mcp-resolve-paths-v1.json</c> or defaults). No hardcoded alias table in code.</summary>
    private static IReadOnlyDictionary<string, string> LoadScopeAliasesMerged()
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var root = ResolveKnowledgeRoot(null);
            var (_, aliasRel) = ReadWorkspacePathsOrDefaults(root);
            MergeScopeAliasFile(Path.Combine(root, KnowledgeDirName, aliasRel.Replace('/', Path.DirectorySeparatorChar)), dict);
        }
        catch (ArgumentException)
        {
            // Canon cannot be resolved — no alias file (e.g. misconfigured env in edge cases).
        }
        catch (IOException)
        {
        }

        return dict;
    }

    private static void MergeScopeAliasFile(string path, IDictionary<string, string> sink)
    {
        if (!File.Exists(path))
            return;

        var text = File.ReadAllText(path, Encoding.UTF8);
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            var parsed = ParseScopeMapLine(line);
            if (parsed is null || !LooksLikeScopeAliasKey(parsed.Value.Item1))
                continue;

            var alias = parsed.Value.Item1.Trim().ToLowerInvariant();
            var canonical = parsed.Value.Item2.Trim().ToLowerInvariant();
            if (alias.Length != 0 && canonical.Length != 0)
                sink[alias] = canonical;
        }
    }

    /// <summary>Alias keys must be single tokens — not filesystem paths (<c>c:\...</c>). Workspace lines in a mis-placed alias file are ignored.</summary>
    private static bool LooksLikeScopeAliasKey(string key)
    {
        var t = key.Trim();
        if (t.Length == 0)
            return false;
        foreach (var c in t)
        {
            if (char.IsLetterOrDigit(c) || c is '.' or '_' or '-')
                continue;
            return false;
        }

        return true;
    }

    /// <summary>Maps legacy shorthand to canonical ids when defined in merged alias dictionary.</summary>
    private static string NormalizeScope(string scope, IReadOnlyDictionary<string, string> aliases)
    {
        var s = scope.Trim().ToLowerInvariant();
        return aliases.TryGetValue(s, out var mapped) ? mapped : s;
    }

    private static string ResolveScope(string? requestedScope, IReadOnlyDictionary<string, string> sections, string workspacePath, IReadOnlyDictionary<string, string> aliases)
    {
        if (!string.IsNullOrWhiteSpace(requestedScope))
            return NormalizeScope(requestedScope, aliases);

        var mappedScope = TryResolveScopeFromWorkspaceMap(workspacePath, sections);
        if (!string.IsNullOrWhiteSpace(mappedScope))
            return NormalizeScope(mappedScope, aliases);

        if (!sections.TryGetValue("active-scope", out var activeScopeContent))
            return NormalizeScope("door-to-singularity", aliases);

        var match = Regex.Match(activeScopeContent, @"current\s*:\s*(?<scope>[A-Za-z0-9._-]+)", RegexOptions.IgnoreCase);
        var raw = match.Success
            ? match.Groups["scope"].Value.Trim().ToLowerInvariant()
            : "door-to-singularity";
        return NormalizeScope(raw, aliases);
    }

    private static string? TryResolveScopeFromWorkspaceMap(string workspacePath, IReadOnlyDictionary<string, string> sections)
    {
        // Prefer machine-local map under canon (single source); else hot sections (legacy).
        var fromFile = TryLoadWorkspaceScopeMapFromWorkLocal();
        var sectionPrimary = sections.TryGetValue("workspace-scope-map-v1", out var pm) ? pm : null;
        var sectionLegacy = sections.TryGetValue("scope-map-v1", out var lm) ? lm : null;
        var mapContent = !string.IsNullOrWhiteSpace(fromFile)
            ? fromFile
            : !string.IsNullOrWhiteSpace(sectionPrimary)
                ? sectionPrimary
                : !string.IsNullOrWhiteSpace(sectionLegacy)
                    ? sectionLegacy
                    : null;

        if (string.IsNullOrWhiteSpace(mapContent))
            return null;

        var normalizedWorkspace = NormalizePathKey(workspacePath);
        var lines = mapContent.Replace("\r\n", "\n").Split('\n');
        string? bestScope = null;
        var bestKeyLength = -1;
        foreach (var line in lines)
        {
            var parsed = ParseScopeMapLine(line);
            if (parsed is null)
                continue;

            var (workspaceKey, scope) = parsed.Value;
            var normalizedKey = NormalizePathKey(workspaceKey);
            if (!IsPrefixPathMatch(normalizedWorkspace, normalizedKey))
                continue;

            if (normalizedKey.Length <= bestKeyLength)
                continue;

            bestKeyLength = normalizedKey.Length;
            bestScope = scope;
        }

        return bestScope;
    }

    /// <summary>Optional map lines (same format as hot section): TOML <c>[workspace]</c>, META JSON, or defaults. Overrides empty/missing hot sections when <see cref="ResolveKnowledgeRoot"/> succeeds.</summary>
    private static string? TryLoadWorkspaceScopeMapFromWorkLocal()
    {
        try
        {
            var root = ResolveKnowledgeRoot(null);
            var (workspaceRel, _) = ReadWorkspacePathsOrDefaults(root);
            var path = Path.Combine(root, KnowledgeDirName, workspaceRel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
                return null;
            return File.ReadAllText(path, Encoding.UTF8);
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static (string workspaceKey, string scope)? ParseScopeMapLine(string rawLine)
    {
        var line = rawLine.Trim();
        if (line.Length == 0 || line.StartsWith('#'))
            return null;

        if (line.StartsWith('-'))
            line = line[1..].Trim();

        var arrowParts = line.Split("=>", StringSplitOptions.TrimEntries);
        if (arrowParts.Length == 2)
            return (arrowParts[0], arrowParts[1].ToLowerInvariant());

        var colonParts = line.Split(':', 2, StringSplitOptions.TrimEntries);
        if (colonParts.Length == 2)
            return (colonParts[0], colonParts[1].ToLowerInvariant());

        var eqParts = line.Split('=', 2, StringSplitOptions.TrimEntries);
        if (eqParts.Length == 2)
            return (eqParts[0], eqParts[1].ToLowerInvariant());

        return null;
    }

    private static string NormalizePathKey(string path) =>
        path.Trim().Replace('/', '\\').TrimEnd('\\');

    private static string ResolveDtsDefaultSectionId(IReadOnlyDictionary<string, string> sections)
    {
        if (sections.ContainsKey("scope-door-to-singularity"))
            return "scope-door-to-singularity";
        if (sections.ContainsKey("scope-current-projects"))
            return "scope-current-projects";
        return "scope-door-to-singularity";
    }

    private static string ResolveScopeSectionId(string resolvedScope, IReadOnlyDictionary<string, string> sections, IReadOnlyDictionary<string, string> aliases)
    {
        if (string.IsNullOrWhiteSpace(resolvedScope))
            return ResolveDtsDefaultSectionId(sections);

        var normalizedScope = NormalizeScope(resolvedScope.Trim(), aliases);
        var genericScopeId = $"scope-{normalizedScope}";
        if (sections.ContainsKey(genericScopeId))
            return genericScopeId;

        if (normalizedScope == "door-to-singularity" && sections.ContainsKey("scope-current-projects"))
            return "scope-current-projects";

        return genericScopeId;
    }

    private static bool IsPrefixPathMatch(string workspacePath, string mapKeyPath)
    {
        if (string.Equals(workspacePath, mapKeyPath, StringComparison.OrdinalIgnoreCase))
            return true;

        if (!workspacePath.StartsWith(mapKeyPath, StringComparison.OrdinalIgnoreCase))
            return false;

        return workspacePath.Length > mapKeyPath.Length && workspacePath[mapKeyPath.Length] == '\\';
    }
}
