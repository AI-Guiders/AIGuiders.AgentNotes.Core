using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentNotes.Core;

/// <summary>Значения по умолчанию для hot-context: бюджеты, списки секций, блоклист тяжёлых L1.
/// Списки L0 / compact suffix / core — из embedded JSON в сборке AgentNotes.Core (<see cref="BundledAgentNotesContent"/> + <c>Resources/hot-context-defaults.json</c>, как <c>BundledAppContent</c> в CascadeIDE).
/// Переопределение рабочей памяти через <c>memory-architecture-v1</c> JSON (см. <see cref="MemoryArchitectureManifestData"/>).</summary>
internal static class HotContextDefaults
{
    /// <summary>Порог предупреждения <c>memory_health</c> (сумма символов hot-секций).</summary>
    public const int HotContextBudgetWarningChars = 6000;

    /// <summary>Порог critical <c>memory_health</c>.</summary>
    public const int HotContextBudgetCriticalChars = 12000;

    private static readonly Lazy<HotContextEmbeddedLists> Lists = new(LoadLists);

    public static string[] DefaultL0Ids => Lists.Value.DefaultL0Ids;

    public static string[] DefaultCompactOrderSuffix => Lists.Value.DefaultCompactOrderSuffix;

    public static string[] RequiredCoreSectionIds => Lists.Value.RequiredCoreSectionIds;

    /// <summary>L1 / тяжёлые секции: не включать в hot, даже если перечислены в L0 манифеста.
    /// Дополнительные id — в JSON <c>hot_context_section_exclusions</c>.</summary>
    public static bool IsBuiltInHotExclusion(string sectionId)
    {
        return sectionId.StartsWith("hpmor-", StringComparison.OrdinalIgnoreCase)
            || sectionId.Equals("it-source-mini-index-v1", StringComparison.Ordinal)
            || sectionId.Equals("knowledge-index-v1", StringComparison.Ordinal)
            || sectionId.Equals("imc-ui-ux-vision-v1", StringComparison.Ordinal)
            || sectionId.Equals("psychology-gender-studies-subdomain-v1", StringComparison.Ordinal)
            || sectionId.Equals("world-human-system-v1", StringComparison.Ordinal)
            || sectionId.Equals("world-human-system-playbook-v1", StringComparison.Ordinal);
    }

    private static HotContextEmbeddedLists LoadLists()
    {
        if (!BundledAgentNotesContent.TryReadEmbeddedText("hot-context-defaults.json", out var text))
            return HardcodedFallbackLists.Instance;

        try
        {
            var dto = JsonSerializer.Deserialize<HotContextDefaultsDto>(text, JsonOptions);
            if (dto is not null
                && dto.DefaultL0Ids is { Length: > 0 }
                && dto.DefaultCompactOrderSuffix is { Length: > 0 }
                && dto.RequiredCoreSectionIds is { Length: > 0 })
            {
                return new HotContextEmbeddedLists(dto.DefaultL0Ids, dto.DefaultCompactOrderSuffix, dto.RequiredCoreSectionIds);
            }
        }
        catch
        {
            // fall through to hardcoded fallback
        }

        return HardcodedFallbackLists.Instance;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private sealed class HotContextDefaultsDto
    {
        [JsonPropertyName("default_l0_ids")]
        public string[]? DefaultL0Ids { get; set; }

        [JsonPropertyName("default_compact_order_suffix")]
        public string[]? DefaultCompactOrderSuffix { get; set; }

        [JsonPropertyName("required_core_section_ids")]
        public string[]? RequiredCoreSectionIds { get; set; }
    }

    private sealed record HotContextEmbeddedLists(
        string[] DefaultL0Ids,
        string[] DefaultCompactOrderSuffix,
        string[] RequiredCoreSectionIds);

    /// <summary>Последний резерв: поток embedded не открылся (сборка/имя ресурса) или JSON внутри сборки не парсится / пустые списки. Не дублирует полный объём списков из JSON в репо.</summary>
    private static class HardcodedFallbackLists
    {
        internal static readonly HotContextEmbeddedLists Instance = new(
            DefaultL0Ids: [
                "baseline-integrity-epistemic-v1",
                "active-scope",
                "current-task"
            ],
            DefaultCompactOrderSuffix: [
                "workspace-scope-map-v1",
                "scope-door-to-singularity",
                "memory-architecture-v1"
            ],
            RequiredCoreSectionIds: [
                "current-task"
            ]);
    }
}

/// <summary>Данные из JSON, на который указывает <c>l0_manifest:</c> в секции memory-architecture-v1.</summary>
internal sealed record MemoryArchitectureManifestData(
    IReadOnlyList<string> L0,
    IReadOnlyList<string>? CompactOrderSuffix,
    int? HotBudgetWarningChars,
    int? HotBudgetCriticalChars,
    IReadOnlyList<string>? HotContextSectionExclusions);
