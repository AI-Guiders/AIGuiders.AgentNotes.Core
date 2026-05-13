# AIGuiders.AgentNotes.Core

.NET-библиотека **`AgentNotes.Core`**: пути к hot-заметкам и канону, чтение/запись `agent-notes.md` и слоя **`knowledge/`**, hot-context, встроенные дефолты (`mcp-resolve-paths`, `hot-context-defaults`). Используется в **[agent-notes-mcp](https://github.com/KarataevDmitry/agent-notes-mcp)** и при необходимости in-proc в IDE.

## Установка

```bash
dotnet add package AIGuiders.AgentNotes.Core
```

[NuGet.org](https://www.nuget.org/packages/AIGuiders.AgentNotes.Core) · исходники: [KarataevDmitry/AIGuiders.AgentNotes.Core](https://github.com/KarataevDmitry/AIGuiders.AgentNotes.Core) · лицензия: [MIT](LICENSE).

Публичные сценарии — через **`NotesStorage`** (см. XML-доки в коде). Тексты KB как контент — не эта библиотека; публичный срез канона — [kb-public](https://github.com/KarataevDmitry/kb-public) (лицензия контента там).

## Сборка пакета (maintainers)

```bash
dotnet pack -c Release -o nupkg
```

Версию и метаданные NuGet по умолчанию правь в **`AgentNotes.Core.csproj`**.

### Публикация на NuGet.org (Trusted Publishing)

Пакет публикуется **без API-ключа**: GitHub Actions → OIDC → [Trusted Publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing) на nuget.org. Учётная запись NuGet.org для выдачи временного ключа — **`LonelySoul`** (имя профиля в шаге `NuGet/login`).

1. На **nuget.org** под пользователем **LonelySoul**: **Trusted Publishing** → новая политика, владелец политики — **ты (индивидуальный пользователь LonelySoul)** или организация, если пакет от неё.
2. Поля политики (регистр не важен): **Repository owner** `KarataevDmitry`, **Repository** `AIGuiders.AgentNotes.Core`, **Workflow file** `nuget-publish.yml` (только имя файла, без пути `.github/workflows/`). **Environment** оставь пустым, если в workflow нет `environment:`.
3. В репозитории workflow: **`.github/workflows/nuget-publish.yml`** — запуск вручную (**Actions → Publish to NuGet → Run workflow**, указать версию) или пуш тега **`v1.2.3`** (в пакет пойдёт версия `1.2.3`).

Первый успешный push активирует политику навсегда (для приватных репо до этого действует окно 7 дней — см. доку NuGet).
