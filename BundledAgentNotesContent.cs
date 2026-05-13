#nullable enable
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text;

namespace AgentNotes.Core;

/// <summary>
/// Встроенные JSON из <c>Resources/</c> (см. <c>EmbeddedResource</c> в AgentNotes.Core.csproj). Имя в манифесте:
/// <c>AgentNotes.Core.Resources.</c> + относительный путь, <c>/</c> → <c>.</c> — тот же приём, что <c>BundledAppContent</c> в CascadeIDE (<c>CascadeIDE.</c> + путь).
/// </summary>
internal static class BundledAgentNotesContent
{
    private static readonly Assembly s_assembly = typeof(BundledAgentNotesContent).Assembly;
    private const string ResourcePrefix = "AgentNotes.Core.Resources.";

    /// <param name="relativePath">Под <c>Resources/</c>, слеши <c>/</c>, напр. <c>hot-context-defaults.json</c>.</param>
    public static bool TryReadEmbeddedText(string relativePath, [NotNullWhen(true)] out string? text)
    {
        text = null;
        var normalized = relativePath.Replace('\\', '/').TrimStart('/');
        if (normalized.Length == 0 || normalized.Contains("..", StringComparison.Ordinal))
            return false;
        var name = ResourcePrefix + normalized.Replace('/', '.');
        using var stream = s_assembly.GetManifestResourceStream(name);
        if (stream is null)
            return false;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        text = reader.ReadToEnd();
        return !string.IsNullOrWhiteSpace(text);
    }
}
