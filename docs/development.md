# Разработка

## Сборка

Нужен .NET 8 SDK.

```bash
dotnet build PilotMCP.slnx
```

Решение включает три проекта: сервер `PilotMCP.csproj`, тесты `PilotMCP.Tests` и генератор документации `DocGen`.

Релизная сборка `dotnet build -c Release` дополнительно кладёт в `artifacts/` два архива под `win-x64`: с .NET внутри (`-selfcontained.zip`) и без него. Версия берётся из `<Version>` в `PilotMCP.csproj`. Отключить упаковку: `-p:SkipReleasePackaging=true`. Собрать только архивы: `dotnet build PilotMCP.csproj -c Release`.

## Тесты

Тесты написаны на xUnit и делятся на два вида:

- **Unit-тесты** не требуют сервера Pilot: построение Markup, разбор XPS, вспомогательные функции, переподключающий прокси.
- **Интеграционные тесты** помечены `[Trait("Category", "Integration")]` и работают с живым сервером Pilot: запускают сервер как MCP-процесс и вызывают инструменты.

Запуск unit-тестов:

```bash
dotnet test PilotMCP.slnx --filter "Category!=Integration"
```

Запуск всех тестов, включая интеграционные:

```bash
dotnet test PilotMCP.slnx
```

Параметры подключения интеграционные тесты берут из файла `PilotMCP.Tests/test.runsettings`: в нём задаются те же переменные `PILOT_*` (адрес сервера, логин, пароль, база, код лицензии, учётная запись администратора). Значения подставьте свои, не публикуйте их в репозитории.

## CI

На каждый push и pull request workflow `ci.yml` собирает решение (Debug) на `windows-latest` и прогоняет unit-тесты. Интеграционные тесты в CI не запускаются.

## Релиз

Релиз собирается по тегу вида `vX.Y.Z` (допускается суффикс, например `-beta.1`):

```bash
git tag v0.1.0
git push origin v0.1.0
```

Workflow `release.yml` прогоняет unit-тесты, собирает zip-архивы и `.mcpb`, создаёт GitHub Release и публикует `server.json` в MCP Registry (вход по OIDC). Версия берётся из тега. Манифест бандла находится в `mcpb/manifest.json`.

## Документация

Сайт собирается [docfx](https://dotnet.github.io/docfx/) из папки `docs/` и публикуется на GitHub Pages workflow `docs.yml`: сборка проверяется на каждом pull request, публикация идёт при push в `master`.

Справочник инструментов (`docs/tools/`) не хранится в репозитории: его генерирует проект `DocGen` по коду сервера. Он обходит классы с `[McpServerToolType]`, методы с `[McpServerTool]`, берёт описания и параметры из `[Description]`, а признак «запись» определяет по вызову `ToolRunner.RunWrite`/`RunWriteAsync` в исходниках `Tools/*.cs`. Результат сверяется со списком заблокированных инструментов режима `PILOT_READONLY` в `README.md`; при расхождении `DocGen` завершается с ошибкой, так что добавляя или переименовывая инструмент записи, обновите README.

`DocGen` не подключается к Pilot и не запускает MCP-сервер.

Локальная сборка и просмотр:

```bash
dotnet run --project DocGen
dotnet tool restore
dotnet docfx docfx.json --serve
```

Сайт откроется на `http://localhost:8080`. Каталоги `docs/tools/` и `_site/` игнорируются git.
