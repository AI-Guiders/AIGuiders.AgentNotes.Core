# AIGuiders.AgentNotes.Core

.NET-библиотека **`AgentNotes.Core`** (2.x): пути к hot-заметкам и **knowledge root**, чтение/запись `agent-notes.md` и **`knowledge/`**, hot-context, локальный TOML (`LocalSettingsLoader`), embedded дефолты (`agent-notes-mcp.defaults.toml`, `hot-context-defaults.json`). Используется в **[agent-notes-mcp](https://github.com/AI-Guiders/agent-notes-mcp)** 2.0 (`--config`) и при необходимости in-proc в IDE.

## Установка

```bash
dotnet add package AIGuiders.AgentNotes.Core
```

[NuGet.org](https://www.nuget.org/packages/AIGuiders.AgentNotes.Core) · исходники: [KarataevDmitry/AIGuiders.AgentNotes.Core](https://github.com/KarataevDmitry/AIGuiders.AgentNotes.Core) · лицензия: [MIT](LICENSE).

Публичные сценарии — через **`NotesStorage`** (см. XML-доки в коде). Тексты KB как контент — не эта библиотека; публичный срез канона — [kb-public](https://github.com/KarataevDmitry/kb-public) (лицензия контента там).

## Сборка и публикация (maintainers)

```bash
dotnet pack -c Release -o nupkg
```

Версия и метаданные по умолчанию — в **`AgentNotes.Core.csproj`**.

Публикация на **nuget.org** через **GitHub Actions** и **[Trusted Publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing)** (OIDC): workflow **[`nuget-publish.yml`](https://github.com/KarataevDmitry/AIGuiders.AgentNotes.Core/blob/main/.github/workflows/nuget-publish.yml)** в этом репозитории. Детали политики nuget.org и запуска — только в **полном каноне** agent-notes (слой **`knowledge/work/`**, не в kb-public); указатель — карточка **agent-notes-mcp** в том же каноне.
