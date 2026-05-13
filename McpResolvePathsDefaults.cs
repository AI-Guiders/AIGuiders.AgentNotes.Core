using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentNotes.Core;

/// <summary>Дефолтные относительные пути для workspace → scope и алиасов: embedded JSON (<see cref="BundledAgentNotesContent"/> + <c>Resources/mcp-resolve-paths-defaults.json</c>). Литералы в коде — только если ресурс не читается или JSON не парсится / не проходит валидацию путей.</summary>
internal static class McpResolvePathsDefaults
{
    private static readonly Lazy<(string WorkspaceScopeMapRelative, string ScopeAliasMapRelative)> Pair = new(Load);

    public static (string WorkspaceScopeMapRelative, string ScopeAliasMapRelative) DefaultsPair => Pair.Value;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static (string, string) Load()
    {
        if (!BundledAgentNotesContent.TryReadEmbeddedText("mcp-resolve-paths-defaults.json", out var text))
            return HardcodedFallback.Pair;

        try
        {
            var dto = JsonSerializer.Deserialize<McpResolvePathsConfigModel>(text, JsonOptions);
            if (dto is not null
                && TryValidateKnowledgePath(dto.WorkspaceScopeMap, out var ws)
                && TryValidateKnowledgePath(dto.ScopeAliasMap, out var al))
            {
                return (ws, al);
            }
        }
        catch
        {
            // fall through to hardcoded fallback
        }

        return HardcodedFallback.Pair;
    }

    /// <summary>Same rules as <c>read_knowledge_file</c> paths: no <c>..</c>, not rooted; trimmed forward slashes.</summary>
    private static bool TryValidateKnowledgePath(string? filePath, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(filePath))
            return false;

        normalized = filePath.Replace('\\', '/').TrimStart('/');
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(normalized))
        {
            normalized = "";
            return false;
        }

        return true;
    }

    private static class HardcodedFallback
    {
        internal static readonly (string WorkspaceScopeMapRelative, string ScopeAliasMapRelative) Pair = (
            "work/local/workspace-scope-map-v1.md",
            "work/local/scope-alias-map-v1.md");
    }
}

/// <summary>JSON shape shared by embedded defaults and optional <c>knowledge/META/mcp-resolve-paths-v1.json</c> on disk.</summary>
internal sealed class McpResolvePathsConfigModel
{
    [JsonPropertyName("workspace_scope_map")]
    public string? WorkspaceScopeMap { get; set; }

    [JsonPropertyName("scope_alias_map")]
    public string? ScopeAliasMap { get; set; }
}
