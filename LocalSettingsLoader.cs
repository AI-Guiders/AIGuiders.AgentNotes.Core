using AgentNotes.Core.Configuration;

namespace AgentNotes.Core;

public static class LocalSettingsLoader
{
    public static LocalSettings Load(string configPath)
    {
        var user = AgentNotesMcpToml.DeserializeFile<AgentNotesMcpConfigDocument>(configPath);
        var defaults = AgentNotesMcpToml.DeserializeEmbedded<AgentNotesMcpConfigDocument>("agent-notes-mcp.defaults.toml");
        return user.Materialize(defaults);
    }
}
