using System.Text;
using System.Text.Json;
using Tomlyn;

namespace AgentNotes.Core.Configuration;

internal static class AgentNotesMcpToml
{
    internal static readonly TomlSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    internal static T Deserialize<T>(string text, string label) where T : class
    {
        try
        {
            return TomlSerializer.Deserialize<T>(text, Options)
                ?? throw new InvalidOperationException($"Invalid TOML ({label}): empty document.");
        }
        catch (TomlException ex)
        {
            throw new InvalidOperationException($"Invalid TOML ({label}): {ex.Message}", ex);
        }
    }

    internal static T DeserializeFile<T>(string path) where T : class
    {
        var fullPath = Path.GetFullPath(path.Trim());
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"Config file not found: {fullPath}", fullPath);
        return Deserialize<T>(File.ReadAllText(fullPath, Encoding.UTF8), fullPath);
    }

    internal static T DeserializeEmbedded<T>(string resourceRelativePath) where T : class
    {
        if (!BundledAgentNotesContent.TryReadEmbeddedText(resourceRelativePath, out var text))
            throw new InvalidOperationException($"Embedded TOML is missing: {resourceRelativePath}");
        return Deserialize<T>(text, resourceRelativePath);
    }
}
