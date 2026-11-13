using Ascon.Pilot.ClientCore.Search;
using Ascon.Pilot.Common.Search;
using Ascon.Pilot.DataClasses;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace PilotMCP;

[McpServerToolType]
public class QueryTools
{
    private readonly PilotConnection _connection;
    public QueryTools(PilotConnection connection) => _connection = connection;

    private static readonly string[] ObjectColumns = ["id", "typeId", "typeName", "parentId", "name", "created", "childrenCount", "filesCount", "attributes"];

    // ========== Простые инструменты поиска ==========

    [McpServerTool, Description(
        "Получить ВСЕ объекты базы (поиск в контексте корня). " +
        "НЕ используйте рекурсивный обход через GetChildren — используйте этот инструмент!")]
    public Task<string> GetAllObjects(
        [Description("Макс. объектов (по умолчанию 10000).")] int maxResults = 10000)
        => ExecuteSearch("GetAllObjects", b =>
            b.Must(ObjectFields.Context.Be(DObject.RootId)), maxResults);

    [McpServerTool, Description(
        "Полнотекстовый поиск объектов по слову или фразе. " +
        "Ищет по всем атрибутам и содержимому. Поддерживает русский язык.")]
    public Task<string> SearchByText(
        [Description("Текст для поиска (слово или фраза).")] string text,
        [Description("Макс. результатов.")] int maxResults = 100)
    {
        var words = QueryHelpers.EscapedWords(text);
        if (words.Length == 0)
            return Task.FromResult(ToolResult.Error("Пустой текст поиска."));
        return ExecuteSearch("SearchByText", b => b.Must(ObjectFields.AllText.ContainsAll(words)), maxResults, info: text);
    }

    [McpServerTool, Description(
        "Поиск объектов внутри папки (рекурсивно по всем вложенным). " +
        "Для получения всех объектов внутри папки: SearchInContext(contextId, query='*').")]
    public Task<string> SearchInContext(
        [Description("GUID папки/объекта, внутри которого искать.")] string contextId,
        [Description("Текст для поиска внутри контекста. '*' = все вложенные объекты.")] string query = "*",
        [Description("Макс. результатов.")] int maxResults = 10000)
    {
        if (!Guid.TryParse(contextId, out var guid) || guid == Guid.Empty)
            return Task.FromResult(ToolResult.Error($"Некорректный GUID: {contextId}"));
        var isWildcard = string.IsNullOrWhiteSpace(query) || query.Trim() == "*";
        return ExecuteSearch("SearchInContext", b =>
        {
            b.Must(ObjectFields.Context.Be(guid));
            if (!isWildcard)
                b.Must(ObjectFields.AllText.ContainsAll(QueryHelpers.EscapedWords(query)));
        }, maxResults, info: $"контекст: {contextId}");
    }

    [McpServerTool, Description(
        "Поиск объектов по типу. typeId — числовой ID типа из GetTypes.")]
    public Task<string> SearchByType(
        [Description("ID типа объекта (число). Получите список типов через GetTypes.")] int typeId,
        [Description("Макс. результатов.")] int maxResults = 1000)
        => ExecuteSearch("SearchByType", b => b.Must(ObjectFields.TypeId.Be(typeId)), maxResults, info: $"typeId={typeId}");

    [McpServerTool, Description(
        "Поиск объектов по значению СТРОКОВОГО атрибута (числовые, даты, состояния, орг. единицы не находятся). " +
        "Значение ищется как точная фраза, в т.ч. из нескольких слов. Например: attributeName='$Title', value='Договор'.")]
    public Task<string> SearchByAttribute(
        [Description("Имя атрибута (например '$Title', 'code', 'mark').")] string attributeName,
        [Description("Значение атрибута для поиска.")] string value,
        [Description("Макс. результатов.")] int maxResults = 100)
    {
        if (string.IsNullOrWhiteSpace(attributeName) || string.IsNullOrWhiteSpace(value))
            return Task.FromResult(ToolResult.Error("Укажите непустые attributeName и value."));
        return ExecuteSearch("SearchByAttribute",
            b => b.Must(AttributeFields.String(attributeName.Trim()).Be(QueryHelpers.QuoteLucene(value.Trim()))),
            maxResults, info: $"{attributeName}={value}");
    }

    [McpServerTool, Description(
        "Поиск объектов по пользовательскому состоянию (UserState). " +
        "stateId — GUID состояния из GetUserStates.")]
    public Task<string> SearchByState(
        [Description("GUID пользовательского состояния.")] string stateId,
        [Description("Макс. результатов.")] int maxResults = 1000)
    {
        if (!Guid.TryParse(stateId, out var guid) || guid == Guid.Empty)
            return Task.FromResult(ToolResult.Error($"Некорректный GUID состояния: {stateId}"));
        return ExecuteSearch("SearchByState", b => b.Must(ObjectFields.StateId.Be(guid)),
            maxResults, info: $"state={stateId}");
    }

    [McpServerTool, Description(
        "Поиск объектов по создателю (personId).")]
    public Task<string> SearchByCreator(
        [Description("ID создателя (personId). Получите через GetPeople.")] int creatorId,
        [Description("Макс. результатов.")] int maxResults = 1000)
        => ExecuteSearch("SearchByCreator", b => b.Must(ObjectFields.CreatorId.Be(creatorId)),
            maxResults, info: $"creator={creatorId}");

    [McpServerTool, Description(
        "Поиск объектов по дате создания (диапазон, время в UTC; дата без времени = UTC).")]
    public Task<string> SearchByDate(
        [Description("Дата ОТ (ISO 8601, например '2025-01-01'). Пусто = без нижней границы.")] string from = "",
        [Description("Дата ДО (ISO 8601, включительно: '2025-01-31' = весь день 31 января). Пусто = без верхней границы.")] string to = "",
        [Description("Макс. результатов.")] int maxResults = 1000)
    {
        if (QueryHelpers.ParseRange(from, to, "ОТ", "ДО", out var dateFrom, out var dateTo) is { } dateError)
            return Task.FromResult(ToolResult.Error(dateError));
        return ExecuteSearch("SearchByDate", b => b.Must(ObjectFields.CreatedDate.BeInRange(dateFrom, dateTo)),
            maxResults, info: $"{from}..{to}");
    }

    [McpServerTool, Description(
        "Комбинированный поиск с несколькими фильтрами одновременно. " +
        "Используйте когда нужно совместить несколько критериев (тип + состояние + дата и т.д.). " +
        "Для простых запросов используйте специализированные инструменты: GetAllObjects, SearchByText, SearchByType и др.")]
    public Task<string> Search(
        [Description("Текстовый поиск. '*' или пусто = без текстового фильтра.")] string query = "*",
        [Description("Макс. результатов.")] int maxResults = 100,
        [Description("GUID объекта-контекста (поиск внутри).")] string context = "",
        [Description("ID типа объекта (0 = любой).")] int typeId = 0,
        [Description("ID создателя (0 = любой).")] int creatorId = 0,
        [Description("GUID состояния.")] string stateId = "",
        [Description("Дата создания ОТ (ISO 8601, UTC).")] string createdFrom = "",
        [Description("Дата создания ДО (ISO 8601, включительно: '2025-01-31' = весь день).")] string createdTo = "",
        [Description("Фильтр по строковому атрибуту: 'имя=значение' (значение ищется как фраза).")] string attribute = "",
        [Description("Исключить замороженные.")] bool excludeFrozen = false)
    {
        var isWildcard = string.IsNullOrWhiteSpace(query) || query.Trim() == "*";
        var words = isWildcard ? Array.Empty<string>() : QueryHelpers.EscapedWords(query);

        // Некорректные фильтры — ошибка, а не молчаливо более широкий поиск.
        Guid ctx = Guid.Empty, st = Guid.Empty;
        if (!string.IsNullOrWhiteSpace(context) && (!Guid.TryParse(context, out ctx) || ctx == Guid.Empty))
            return Task.FromResult(ToolResult.Error($"Некорректный GUID контекста: {context}"));
        if (!string.IsNullOrWhiteSpace(stateId) && (!Guid.TryParse(stateId, out st) || st == Guid.Empty))
            return Task.FromResult(ToolResult.Error($"Некорректный GUID состояния: {stateId}"));
        var hasDates = !string.IsNullOrWhiteSpace(createdFrom) || !string.IsNullOrWhiteSpace(createdTo);
        DateTime from = DateTime.MinValue, to = DateTime.MaxValue;
        if (hasDates && QueryHelpers.ParseRange(createdFrom, createdTo, "createdFrom", "createdTo", out from, out to) is { } dateError)
            return Task.FromResult(ToolResult.Error(dateError));
        string? attrName = null, attrValue = null;
        if (!string.IsNullOrWhiteSpace(attribute))
        {
            var parts = attribute.Split('=', 2);
            if (parts.Length != 2 || parts[0].Trim().Length == 0 || parts[1].Trim().Length == 0)
                return Task.FromResult(ToolResult.Error($"Фильтр атрибута должен быть вида 'имя=значение': {attribute}"));
            (attrName, attrValue) = (parts[0].Trim(), parts[1].Trim());
        }

        return ExecuteSearch("Search", b =>
        {
            if (words.Length > 0)
                b.Must(ObjectFields.AllText.ContainsAll(words));
            if (ctx != Guid.Empty)
                b.Must(ObjectFields.Context.Be(ctx));
            if (typeId > 0)
                b.Must(ObjectFields.TypeId.Be(typeId));
            if (creatorId > 0)
                b.Must(ObjectFields.CreatorId.Be(creatorId));
            if (st != Guid.Empty)
                b.Must(ObjectFields.StateId.Be(st));
            if (hasDates)
                b.Must(ObjectFields.CreatedDate.BeInRange(from, to));
            if (attrName != null)
                b.Must(AttributeFields.String(attrName).Be(QueryHelpers.QuoteLucene(attrValue!)));
            if (excludeFrozen)
                b.MustNot(ObjectFields.ObjectState.Be(ObjectState.Frozen));
        }, maxResults, info: query);
    }

    // ========== Общий метод выполнения поиска ==========

    private async Task<string> ExecuteSearch(string toolName, Action<IQueryBuilder> buildQuery, int maxResults,
        string? info = null, string? typeFilter = null)
    {
        try
        {
            _connection.EnsureConnected();
            maxResults = Math.Clamp(maxResults, 1, 10000);

            var qb = QueryBuilderFactory.CreateEmptyQueryBuilder();
            buildQuery(qb);
            var (luceneQuery, _) = qb.Build();

            // Если QueryBuilder пустой (GetAllObjects) — используем wildcard
            var searchString = string.IsNullOrWhiteSpace(luceneQuery) ? "*" : luceneQuery;

            var searchDef = new DSearchDefinition
            {
                Id = Guid.NewGuid(),
                Request =
                {
                    MaxResults = maxResults,
                    SearchKind = SearchKind.Custom,
                    SearchString = searchString
                }
            };

            if (!string.IsNullOrEmpty(typeFilter))
                searchDef.Request.TypeFilter = typeFilter;

            var logArgs = $"searchString='{searchString}', maxResults={maxResults}, info='{info}'";
            ToolLogger.Log(toolName, logArgs, "Executing...");

            // Таймаут — исключение из SearchAsync (попадёт в catch как ошибка, а не «пусто»).
            // Один повтор при пустом результате (индекс может отставать), пауза только перед повтором.
            DSearchResult? result = null;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                if (attempt > 0)
                {
                    ToolLogger.Log(toolName, logArgs, "Retry...");
                    await Task.Delay(500);
                    searchDef = new DSearchDefinition
                    {
                        Id = Guid.NewGuid(),
                        Request = { MaxResults = maxResults, SearchKind = SearchKind.Custom, SearchString = searchString }
                    };
                    if (!string.IsNullOrEmpty(typeFilter))
                        searchDef.Request.TypeFilter = typeFilter;
                }
                result = await _connection.SearchAsync(searchDef)
                    ?? throw new InvalidOperationException("Сервер не вернул результат поиска.");
                if (result.Found != null && result.Found.Any())
                    break;
            }

            if (result?.Found == null || !result.Found.Any())
            {
                ToolLogger.Log(toolName, logArgs, "0 results");
                return ToolResult.Ok("Поиск не дал результатов.");
            }

            var foundIds = result.Found.ToArray();
            var objects = await GetObjectsBatchedAsync(foundIds);

            var (path, count) = WriteObjects(objects, "search");
            var msg = $"Найдено {count} объектов" + (info != null ? $" ({info})" : "");
            if (result.TotalResults > foundIds.Length)
                msg += $". Результат неполный: сервер нашёл {result.TotalResults}, возвращено {foundIds.Length} (maxResults={maxResults}). Сузьте запрос";
            if (count < foundIds.Length)
                msg += $". Загружено {count} из {foundIds.Length} найденных (остальные недоступны или удалены)";
            ToolLogger.Log(toolName, logArgs, msg);
            return ToolResult.File(path, count, ObjectColumns, msg);
        }
        catch (Exception ex)
        {
            ToolLogger.LogError(toolName, info ?? "", ex);
            return ToolResult.Error($"Ошибка поиска: {ex.InnerException?.Message ?? ex.Message}");
        }
    }

    private const int GetObjectsBatchSize = 500;

    /// <summary>Загружает объекты порциями, чтобы не упереться в лимит размера сообщения транспорта.</summary>
    private async Task<List<DObject>> GetObjectsBatchedAsync(Guid[] ids)
    {
        var all = new List<DObject>(ids.Length);
        foreach (var chunk in ids.Chunk(GetObjectsBatchSize))
        {
            var part = await _connection.ServerApi.GetObjectsAsync(chunk);
            if (part != null) all.AddRange(part);
        }
        return all;
    }

    /// <summary>Загружает потомков объекта-родителя и пишет их в файл; сообщает, если часть не загрузилась.</summary>
    private async Task<string> LoadChildrenAsync(string toolName, Guid parentGuid, string parentLabel, string prefix,
        Func<int, string> okMessage, string noParentMessage, string emptyMessage)
    {
        try
        {
            _connection.EnsureConnected();
            var parents = await _connection.ServerApi.GetObjectsAsync(new[] { parentGuid });
            if (parents == null || parents.Count == 0)
                return ToolResult.Error(noParentMessage);

            var parent = parents[0];
            if (!parent.Children.Any())
                return ToolResult.Ok(emptyMessage);

            var childIds = parent.Children.Select(c => c.ObjectId).ToArray();
            var children = await GetObjectsBatchedAsync(childIds);

            var (path, count) = WriteObjects(children, prefix);
            var msg = okMessage(count);
            if (count < childIds.Length)
                msg += $". Загружено {count} из {childIds.Length} (остальные недоступны или удалены)";
            return ToolResult.File(path, count, ObjectColumns, msg);
        }
        catch (Exception ex)
        {
            ToolLogger.LogError(toolName, parentLabel, ex);
            return ToolResult.Error($"Ошибка: {ex.InnerException?.Message ?? ex.Message}");
        }
    }

    [McpServerTool, Description("Получить дочерние объекты. Результат записывается в файл (JSONL).")]
    public Task<string> GetChildren(
        [Description("GUID родительского объекта. Корень базы: 00000001-0001-0001-0001-000000000001")] string parentId)
    {
        if (!Guid.TryParse(parentId, out var guid) || guid == Guid.Empty)
            return Task.FromResult(ToolResult.Error($"Некорректный GUID: {parentId}"));
        return LoadChildrenAsync("GetChildren", guid, parentId, "children",
            count => $"{count} дочерних объектов у {parentId}",
            $"Объект {parentId} не найден.", $"У объекта {parentId} нет дочерних объектов.");
    }

    [McpServerTool, Description("Получить корневые объекты базы Pilot. Результат записывается в файл (JSONL).")]
    public Task<string> GetRootObjects()
        => LoadChildrenAsync("GetRootObjects", DObject.RootId, "root", "root",
            count => $"{count} корневых объектов",
            "Корневые объекты не найдены.", "Корень пуст.");

    [McpServerTool, Description("Получить несколько объектов по их GUID и записать в файл.")]
    public async Task<string> GetObjectsToFile(
        [Description("GUID идентификаторы через запятую")] string objectIds)
    {
        var ids = objectIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (ids.Length == 0)
            return ToolResult.Error("Не указаны GUID объектов.");
        var guids = new List<Guid>();
        foreach (var id in ids)
        {
            if (Guid.TryParse(id, out var guid)) guids.Add(guid);
            else return ToolResult.Error($"Некорректный GUID: {id}");
        }

        try
        {
            _connection.EnsureConnected();
            var objects = await GetObjectsBatchedAsync(guids.ToArray());
            var (path, count) = WriteObjects(objects, "objects");
            var msg = $"{count} объектов загружено";
            if (count < guids.Count)
                msg += $" из {guids.Count} запрошенных (остальные не найдены или недоступны)";
            return ToolResult.File(path, count, ObjectColumns, msg);
        }
        catch (Exception ex)
        {
            ToolLogger.LogError("GetObjectsToFile", objectIds, ex);
            return ToolResult.Error($"Ошибка: {ex.InnerException?.Message ?? ex.Message}");
        }
    }

    [McpServerTool, Description("Получить список типов объектов и записать в файл (JSONL). Работает из кэша — не требует активного соединения.")]
    public string GetTypes()
    {
        var types = _connection.Metadata.Types;
        string[] columns = ["id", "name", "title", "hasFiles", "isProject", "kind", "attributeNames"];

        var (path, count) = TempFiles.WriteJsonl(types, t => new Dictionary<string, object>
        {
            ["id"] = t.Id,
            ["name"] = t.Name,
            ["title"] = t.Title ?? "",
            ["hasFiles"] = t.HasFiles,
            ["isProject"] = t.IsProject,
            ["kind"] = t.Kind.ToString(),
            ["attributeNames"] = string.Join(", ", t.Attributes.Select(a => a.Name))
        }, "types");

        return ToolResult.File(path, count, columns, $"{count} типов объектов");
    }

    [McpServerTool, Description("Получить список всех пользователей и записать в файл (JSONL).")]
    public async Task<string> GetPeople()
    {
        _connection.EnsureConnected();
        var people = await _connection.ServerApi.LoadPeopleAsync();
        string[] columns = ["id", "login", "displayName", "email", "phone", "accountState", "isAdmin", "isDeleted", "comment", "positions"];

        var (path, count) = TempFiles.WriteJsonl(people, p => new Dictionary<string, object>
        {
            ["id"] = p.Id,
            ["login"] = p.Login ?? "",
            ["displayName"] = p.DisplayName ?? "",
            ["email"] = p.Email ?? "",
            ["phone"] = p.Phone ?? "",
            ["accountState"] = p.AccountState.ToString(),
            ["isAdmin"] = p.IsAdmin,
            ["isDeleted"] = p.IsDeleted,
            ["comment"] = p.Comment ?? "",
            ["positions"] = string.Join(", ", (p.Positions ?? new List<int>()).Select(pos => $"#{pos}"))
        }, "people");

        return ToolResult.File(path, count, columns, $"{count} пользователей");
    }

    [McpServerTool, Description("Получить список всех организационных единиц и записать в файл (JSONL).")]
    public async Task<string> GetOrganisationUnits()
    {
        _connection.EnsureConnected();
        var units = await _connection.ServerApi.LoadOrganisationUnitsAsync();
        string[] columns = ["id", "title", "kind", "parentId", "personId", "isDeleted", "isCanceled", "vicePersonIds", "groupPersonIds"];

        var (path, count) = TempFiles.WriteJsonl(units, u => new Dictionary<string, object>
        {
            ["id"] = u.Id,
            ["title"] = u.Title ?? "",
            ["kind"] = u.Kind.ToString(),
            ["parentId"] = u.ParentId,
            ["personId"] = u.Person,
            ["isDeleted"] = u.IsDeleted,
            ["isCanceled"] = u.IsCanceled,
            ["vicePersonIds"] = string.Join(", ", (u.VicePersons ?? new List<int>()).Select(v => $"#{v}")),
            ["groupPersonIds"] = string.Join(", ", (u.GroupPersons ?? new List<int>()).Select(g => $"#{g}"))
        }, "orgunits");

        return ToolResult.File(path, count, columns, $"{count} организационных единиц");
    }

    [McpServerTool, Description("Получить список пользовательских состояний (UserStates) — для задач и workflow.")]
    public string GetUserStates()
    {
        var states = _connection.Metadata.UserStates;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Пользовательские состояния ({states.Count}):");
        foreach (var s in states)
        {
            var deleted = s.IsDeleted ? " [удалено]" : "";
            var system = s.IsSystemState ? " [системное]" : "";
            var completion = s.IsCompletionState ? " [завершающее]" : "";
            sb.AppendLine($"  - {s.Id}: {s.Title} (name={s.Name}, цвет={s.Color}){deleted}{system}{completion}");
        }
        return ToolResult.Ok(sb.ToString());
    }

    [McpServerTool, Description("Получить машины состояний (StateMachines) — workflow и переходы между состояниями.")]
    public string GetStateMachines()
    {
        var machines = _connection.Metadata.StateMachines;
        var stateMap = _connection.Metadata.UserStates.ToDictionary(s => s.Id, s => s.Title);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Машины состояний ({machines.Count}):");
        foreach (var m in machines)
        {
            sb.AppendLine($"\n  === {m.Title} (ID: {m.Id}) ===");
            if (m.StateTransitions != null)
            {
                foreach (var kvp in m.StateTransitions)
                {
                    var fromName = stateMap.TryGetValue(kvp.Key, out var fn) ? fn : kvp.Key.ToString();
                    var targets = kvp.Value
                        .Select(t => stateMap.TryGetValue(t.StateTo, out var tn) ? $"{tn} ({t.DisplayName})" : t.StateTo.ToString());
                    sb.AppendLine($"    {fromName} -> [{string.Join(", ", targets)}]");
                }
            }
        }
        return ToolResult.Ok(sb.ToString());
    }


    [McpServerTool, Description("Получить автоматизации (AutomationConfig), их триггеры (Triggers) и C# вставки/код (CSharpCode).")]
    public string GetAutomationConfigs()
    {
        var automationConfigs = _connection.Metadata.AutomationConfig;
        string[] columns = ["name", "isPaused", "triggers", "cSharpCode",];

        var (path, count) = TempFiles.WriteJsonl(automationConfigs, cnf => new Dictionary<string, object>
        {
            ["name"] = cnf.Name,
            ["isPaused"] = cnf.IsPaused,
            ["triggers"] = cnf.Triggers,
            ["cSharpCode"] = cnf.CSharpCode,
        },"automations");

        return ToolResult.File(path, count, columns, $"{count} автоматизаций");
    }

    private (string path, int count) WriteObjects(IList<DObject> objects, string prefix)
    {
        return TempFiles.WriteJsonl(objects, obj => Helpers.ObjectToDict(_connection.Metadata, obj), prefix);
    }
}
