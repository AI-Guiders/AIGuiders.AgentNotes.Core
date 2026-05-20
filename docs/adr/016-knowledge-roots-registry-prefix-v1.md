# ADR 016: Knowledge-roots registry — префиксы и соглашения (не каталог всех файлов)

**Статус:** Accepted  
**Дата:** 2026-05-19  
**Компонент:** `AgentNotes.Core` (`NotesStorage.KnowledgeRootsOverlay`)  
**Расширяет:** agent-notes-mcp [ADR 015](../../agent-notes-mcp/docs/adr/015-multi-root-read-only-knowledge-routing-v1.md) (multi-root + реестр `work/local/knowledge-roots-index-v1.md`)  
**Связано:** KB [011](https://github.com/AI-Guiders/kb/blob/main/knowledge/adr/011-aiguiders-org-collaborative-kb-repo-v1.md) (org KB), `work/org/scope-contour-map-v1.md` (personal scope → group catalog), `workspace-scope-map` (диск → slice)

---

## Контекст

После появления **group KB** (`knowledge_root_id=group` в TOML) участники путают два механизма:

| Механизм | Где | Что даёт |
|----------|-----|----------|
| **TOML** `[[knowledge.read_only]]` | `agent-notes-mcp.toml` | **Весь** клон `{ORG}/kb` доступен для `read_knowledge_file(..., knowledge_root_id=group)` — **любой** путь под `knowledge/` |
| **`knowledge-roots-index-v1.md`** | personal `work/local/` | Подсказки для **`route_context`**: подмешать hot `knowledge-roots-routing-v1` + короткий preview, если запрос «про group/roots» или совпала строка реестра |

Реестр **не** whitelist доступа. Запись в group по-прежнему запрещена; чтение не требует строки в индексе.

**Симптом:** кажется, что для каждого файла в group нужна строка `path => group`. Индекс раздувается; его путают с **scope contour map** (`personal scope` → имя каталога в org).

**Текущая реализация (Core 2.1.1):** строка реестра — **точный** относительный путь файла (`group/smoke-test-v1.md => group`). Совпадение по **префиксу каталога** для overlay **нет**.

---

## Проблема

1. **Масштаб:** десятки карточек под `work/projects/<group-scope>/` — перечислять в индексе бессмысленно.
2. **Дублирование:** `scope-contour-map` уже задаёт mapping slug'ов; дублировать каждый файл в roots-index — шум.
3. **Документация:** в KB написано «какие файлы читать через group» — это неверно трактуется как обязательный каталог.

---

## Решение (идея)

### 1. Разделить три «карты» (не смешивать)

| Карта | Вопрос | Где |
|-------|--------|-----|
| `workspace-scope-map` | `путь на диске` → **slice** | `work/local/` |
| `scope-alias-map` | короткий токен → **тот же** slice | `work/local/` |
| **scope-contour-map** | personal **scope** → **каталог в group** | `work/org/` (+ seed в org) |
| **knowledge-roots-index** | **подсказки route_context** + редкие якоря | `work/local/` |

Contour map **не** заменяется roots-index и **не** дублируется в нём построчно.

### 2. Формат реестра v2: точный путь **или** префикс

Одна строка — одна запись (как сейчас), синтаксис:

```text
# точный файл (как v2.1.1)
group/smoke-test-v1.md => group

# префикс каталога — trailing slash обязателен
work/org/ => group
work/projects/aiguiders-open/ => group
```

**Правила:**

- Путь **без** завершающего `/` — только **этот** файл участвует в scoring overlay.
- Путь **с** завершающим `/` — **префикс**: любой `knowledge/{prefix}…` даёт hit, если токены запроса пересекаются с префиксом или root id.
- Строки с `#` — комментарии; допустим блок `# conventions:` (только для человека, Core не парсит prose).
- `user` / пустой root — по-прежнему пропускаем (primary).

**Не вводим** glob `*` в v1 — только trailing `/`, чтобы не путать с файлами.

### 3. Минимальный индекс maintainer'а (норма)

```text
group/smoke-test-v1.md => group
work/org/ => group
work/projects/<group-scope-dir>/ => group
```

Имена `<group-scope-dir>` — из **одной** таблицы `work/org/scope-contour-map-v1.md`, не из десятков карточек.

### 4. Соглашения в hot (не в индексе)

Секция `knowledge-roots-routing-v1` в `agent-notes.md` (L0, выше `public-cut`) — **стабильный контракт** для агента:

- TOML `group` → весь clone; явное чтение: `read_knowledge_file(..., knowledge_root_id=group)`.
- Open stack / org cards: `work/projects/<group-scope-dir>/` (см. contour map в group: `work/org/scope-contour-map-v1.md`).
- Personal те же смыслы: `work/projects/<personal-scope>/`.
- Реестр — **якоря** для `route_context`, не полный каталог.

### 5. Поведение `route_context` после префиксов

Без изменения контракта read:

- При hit по префиксу overlay показывает **префикс** и root id, preview — **первый существующий** файл под префиксом (например `README.md` в каталоге) или только routing hint без preview, если файла нет.
- Лимит overlay entries — как сейчас (`MaxRegistryOverlayEntries = 3`).
- Scoring: токены запроса (`aiguiders-open`, `open stack`, `scope contour`) матчят сегменты пути префикса.

**Не делаем:** автоматический выбор `knowledge_root_id` в `read_knowledge_file` без указания root — агент по-прежнему передаёт `group` явно.

---

## Последствия

| Плюс | Минус |
|------|--------|
| Индекс остаётся коротким (3–6 строк) | Нужен релиз Core + bump MCP |
| Ясная граница: TOML = доступ, index = подсказки | Старые индексы без `/` на каталогах — только exact match (совместимо) |
| Contour map остаётся SSOT для имён scope | Preview по префиксу — эвристика (README), не «все файлы» |

---

## План внедрения

1. **ADR accepted** (этот документ).
2. **AgentNotes.Core:** `KnowledgeRootRegistryEntry` + флаг `IsPrefix`; парсинг trailing `/`; `ScoreKnowledgeRootRegistryEntry` — match path prefix; preview — optional `README.md` under prefix.
3. **Тесты** в `AgentNotesMcp.Tests`: prefix hit on query `aiguiders-open`; exact smoke unchanged.
4. **KB / шаблоны:** `template-clean-setup-knowledge-roots-index-v1.md`, `work/local/README.md`, hot example — префиксы + conventions.
5. **Personal index:** smoke + `work/org/` + один префикс projects; убрать лишние per-file строки.
6. Версия NuGet **2.1.2** (patch-minor), MCP с `UseProjectReference` или bump package ref.

---

## Критерии принятия

- [x] `work/projects/foo/` с trailing slash даёт overlay hit на запросе с токеном сегмента (`aiguiders-open`).
- [x] `group/smoke-test-v1.md => group` без slash — только exact, как раньше.
- [x] Документация KB не говорит «перечисли все файлы group в индексе».
- [x] Contour map не дублируется в roots-index построчно.

---

## Открытые вопросы

- Нужен ли отдельный синтаксис `=> group (prefix)` вместо trailing `/`? **Пока нет** — slash совпадает с unix-путём каталога.
- Стоит ли поднимать `MaxRegistryOverlayEntries` с 3 до 5 при префиксах? **Пока нет** — префиксов мало.
