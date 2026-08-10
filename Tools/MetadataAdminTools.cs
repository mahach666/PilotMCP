using Ascon.Pilot.DataClasses;
using Ascon.Pilot.DataClasses.Utils;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;

namespace PilotMCP;

/// <summary>
/// Инструменты редактирования метаданных базы через админский API (LoginServerAdministrator):
/// автоматизации, типы, атрибуты, состояния и машины состояний.
/// Каждая операция: чтение свежих метаданных → точечное изменение → запись целиком.
/// </summary>
[McpServerToolType]
public class MetadataAdminTools
{
    private readonly PilotConnection _connection;
    public MetadataAdminTools(PilotConnection connection) => _connection = connection;

    // ===== Автоматизации =====

    [McpServerTool, Description(
        "Создать автоматизацию (AutomationConfig). Требует учётку администратора сервера " +
        "(PILOT_ADMIN_LOGIN/PILOT_ADMIN_PASSWORD). Triggers и CSharpCode — в том же формате, " +
        "что возвращает GetAutomationConfigs. Перед созданием код можно проверить через ValidateAutomationScript.")]
    public Task<string> CreateAutomationConfig(
        [Description("Имя автоматизации (уникальное)")] string name,
        [Description("Триггеры (строка, формат как в GetAutomationConfigs)")] string triggers,
        [Description("C#-код автоматизации")] string cSharpCode,
        [Description("Создать приостановленной (true/false)")] bool isPaused = false)
    {
        return ToolRunner.RunWriteAsync(_connection, "CreateAutomationConfig", $"name={name}", async () =>
        {
            var version = await _connection.UpdateMetadataViaAdminAsync(meta =>
            {
                if (meta.AutomationConfig.Any(a => a.Name == name))
                    throw new InvalidOperationException($"Автоматизация '{name}' уже существует.");

                meta.AutomationConfig.Add(new AutomationScript
                {
                    Name = name,
                    Triggers = triggers,
                    CSharpCode = cSharpCode,
                    IsPaused = isPaused,
                });
            });
            return ToolResult.Ok($"Автоматизация '{name}' создана. Версия метаданных: {version}.");
        });
    }

    [McpServerTool, Description(
        "Изменить автоматизацию (AutomationConfig) по имени. Пустые параметры не меняются. " +
        "Требует учётку администратора сервера.")]
    public Task<string> UpdateAutomationConfig(
        [Description("Имя автоматизации")] string name,
        [Description("Новое имя (пусто = не менять)")] string newName = "",
        [Description("Новые триггеры (пусто = не менять)")] string triggers = "",
        [Description("Новый C#-код (пусто = не менять)")] string cSharpCode = "",
        [Description("Приостановить: true/false (пусто = не менять)")] string isPaused = "")
    {
        return ToolRunner.RunWriteAsync(_connection, "UpdateAutomationConfig", $"name={name}", async () =>
        {
            var changed = new List<string>();
            var version = await _connection.UpdateMetadataViaAdminAsync(meta =>
            {
                var script = meta.AutomationConfig.FirstOrDefault(a => a.Name == name)
                    ?? throw new InvalidOperationException($"Автоматизация '{name}' не найдена.");

                if (!string.IsNullOrEmpty(newName) && newName != name)
                {
                    if (meta.AutomationConfig.Any(a => a.Name == newName))
                        throw new InvalidOperationException($"Автоматизация '{newName}' уже существует.");
                    script.Name = newName;
                    changed.Add("name");
                }
                if (!string.IsNullOrEmpty(triggers)) { script.Triggers = triggers; changed.Add("triggers"); }
                if (!string.IsNullOrEmpty(cSharpCode)) { script.CSharpCode = cSharpCode; changed.Add("cSharpCode"); }
                if (!string.IsNullOrEmpty(isPaused))
                {
                    script.IsPaused = ParseBool(isPaused, "isPaused");
                    changed.Add("isPaused");
                }

                if (changed.Count == 0)
                    throw new InvalidOperationException("Не указаны поля для обновления.");
            });
            return ToolResult.Ok($"Автоматизация '{name}' обновлена ({string.Join(", ", changed)}). Версия метаданных: {version}.");
        });
    }

    [McpServerTool, Description("Удалить автоматизацию (AutomationConfig) по имени. Требует учётку администратора сервера.")]
    public Task<string> DeleteAutomationConfig(
        [Description("Имя автоматизации")] string name)
    {
        return ToolRunner.RunWriteAsync(_connection, "DeleteAutomationConfig", $"name={name}", async () =>
        {
            var version = await _connection.UpdateMetadataViaAdminAsync(meta =>
            {
                var script = meta.AutomationConfig.FirstOrDefault(a => a.Name == name)
                    ?? throw new InvalidOperationException($"Автоматизация '{name}' не найдена.");
                meta.AutomationConfig.Remove(script);
            });
            return ToolResult.Ok($"Автоматизация '{name}' удалена. Версия метаданных: {version}.");
        });
    }

    [McpServerTool, Description(
        "Проверить C#-код автоматизации на сервере (компиляция) без сохранения. " +
        "Требует учётку администратора сервера.")]
    public Task<string> ValidateAutomationScript(
        [Description("C#-код автоматизации")] string cSharpCode)
    {
        return ToolRunner.RunAsync(_connection, "ValidateAutomationScript", $"len={cSharpCode?.Length}", async () =>
        {
            var result = await _connection.WithAdminApiAsync(a => a.ValidateCSharpScriptAsync(cSharpCode!));
            if (result.IsSuccess)
                return ToolResult.Ok("Код валиден.");

            var diagnostics = result.Diagnostics != null
                ? string.Join("\n", result.Diagnostics.Select(d => $"[{d.Severity}] позиция {d.Location?.Start}: {d.Message}"))
                : "(нет диагностики)";
            return ToolResult.Error($"Код не прошёл проверку:\n{diagnostics}");
        });
    }

    // ===== Типы =====

    [McpServerTool, Description(
        "Создать тип объекта (MType). Требует учётку администратора сервера. " +
        "parentTypeNames — имена родительских типов через запятую: новый тип будет добавлен в их Children.")]
    public Task<string> CreateType(
        [Description("Имя типа (латиницей, уникальное)")] string name,
        [Description("Отображаемое название")] string title,
        [Description("Вид типа: User, System, Service")] string kind = "User",
        [Description("Тип может содержать файлы (true/false)")] bool hasFiles = false,
        [Description("Тип монтируемый — Pilot Storage (true/false)")] bool isMountable = false,
        [Description("Служебный тип (true/false)")] bool isService = false,
        [Description("Проектный тип (true/false)")] bool isProject = false,
        [Description("Имена родительских типов через запятую (пусто = корневой)")] string parentTypeNames = "",
        [Description("Порядок сортировки (-1 = в конец)")] int sort = -1)
    {
        return ToolRunner.RunWriteAsync(_connection, "CreateType", $"name={name}", async () =>
        {
            int newId = 0;
            var version = await _connection.UpdateMetadataViaAdminAsync(meta =>
            {
                if (meta.Types.Any(t => t.Name == name))
                    throw new InvalidOperationException($"Тип '{name}' уже существует.");
                if (!Enum.TryParse<TypeKind>(kind, true, out var typeKind))
                    throw new InvalidOperationException($"Неизвестный вид типа '{kind}'. Допустимо: {string.Join(", ", Enum.GetNames<TypeKind>())}.");

                var parents = ResolveTypes(meta, parentTypeNames);

                newId = meta.Types.Count > 0 ? meta.Types.Max(t => t.Id) + 1 : 1;
                var newType = new MType
                {
                    Id = newId,
                    Name = name,
                    Title = title,
                    Kind = typeKind,
                    HasFiles = hasFiles,
                    IsMountable = isMountable,
                    IsService = isService,
                    IsProject = isProject,
                    Sort = sort >= 0 ? sort : (meta.Types.Count > 0 ? meta.Types.Max(t => t.Sort) + 1 : 0),
                };
                meta.Types.Add(newType);

                foreach (var parent in parents)
                {
                    if (!parent.Children.Contains(newId))
                        parent.Children.Add(newId);
                }
            });
            return ToolResult.Ok($"Тип '{name}' создан (ID: {newId}). Версия метаданных: {version}.");
        });
    }

    [McpServerTool, Description(
        "Изменить тип объекта (MType) по имени. Пустые параметры не меняются. Требует учётку администратора сервера.")]
    public Task<string> UpdateType(
        [Description("Имя типа")] string name,
        [Description("Новое отображаемое название (пусто = не менять)")] string title = "",
        [Description("Может содержать файлы: true/false (пусто = не менять)")] string hasFiles = "",
        [Description("Монтируемый: true/false (пусто = не менять)")] string isMountable = "",
        [Description("Порядок сортировки (пусто = не менять)")] string sort = "",
        [Description("ID машины состояний (Guid; 'none' = отвязать; пусто = не менять)")] string stateMachineId = "")
    {
        return ToolRunner.RunWriteAsync(_connection, "UpdateType", $"name={name}", async () =>
        {
            var changed = new List<string>();
            var version = await _connection.UpdateMetadataViaAdminAsync(meta =>
            {
                var type = ResolveType(meta, name);

                if (!string.IsNullOrEmpty(title)) { type.Title = title; changed.Add("title"); }
                if (!string.IsNullOrEmpty(hasFiles)) { type.HasFiles = ParseBool(hasFiles, "hasFiles"); changed.Add("hasFiles"); }
                if (!string.IsNullOrEmpty(isMountable)) { type.IsMountable = ParseBool(isMountable, "isMountable"); changed.Add("isMountable"); }
                if (!string.IsNullOrEmpty(sort))
                {
                    if (!int.TryParse(sort, out var sortVal))
                        throw new InvalidOperationException($"Некорректное значение sort: '{sort}'.");
                    type.Sort = sortVal;
                    changed.Add("sort");
                }
                if (!string.IsNullOrEmpty(stateMachineId))
                {
                    if (stateMachineId.Equals("none", StringComparison.OrdinalIgnoreCase))
                        type.StateMachineId = Guid.Empty;
                    else
                    {
                        if (!Guid.TryParse(stateMachineId, out var smId))
                            throw new InvalidOperationException($"Некорректный Guid машины состояний: '{stateMachineId}'.");
                        if (meta.StateMachines.All(m => m.Id != smId))
                            throw new InvalidOperationException($"Машина состояний {smId} не найдена.");
                        type.StateMachineId = smId;
                    }
                    changed.Add("stateMachineId");
                }

                if (changed.Count == 0)
                    throw new InvalidOperationException("Не указаны поля для обновления.");
            });
            return ToolResult.Ok($"Тип '{name}' обновлён ({string.Join(", ", changed)}). Версия метаданных: {version}.");
        });
    }

    [McpServerTool, Description(
        "Удалить тип объекта (пометить IsDeleted, как это делает myAdmin). Требует учётку администратора сервера.")]
    public Task<string> DeleteType(
        [Description("Имя типа")] string name)
    {
        return ToolRunner.RunWriteAsync(_connection, "DeleteType", $"name={name}", async () =>
        {
            var version = await _connection.UpdateMetadataViaAdminAsync(meta =>
            {
                var type = ResolveType(meta, name);
                if (type.Kind != TypeKind.User)
                    throw new InvalidOperationException($"Тип '{name}' — {type.Kind}, удалять можно только пользовательские (User) типы.");
                type.IsDeleted = true;
            });
            return ToolResult.Ok($"Тип '{name}' помечен удалённым. Версия метаданных: {version}.");
        });
    }

    [McpServerTool, Description(
        "Добавить дочерние типы к типу (Children — какие типы можно создавать внутри). " +
        "Требует учётку администратора сервера.")]
    public Task<string> AddTypeChildren(
        [Description("Имя родительского типа")] string parentTypeName,
        [Description("Имена дочерних типов через запятую")] string childTypeNames)
    {
        return ToolRunner.RunWriteAsync(_connection, "AddTypeChildren", $"parent={parentTypeName}, children={childTypeNames}", async () =>
        {
            var added = new List<string>();
            var version = await _connection.UpdateMetadataViaAdminAsync(meta =>
            {
                var parent = ResolveType(meta, parentTypeName);
                var children = ResolveTypes(meta, childTypeNames);
                if (children.Count == 0)
                    throw new InvalidOperationException("Не указаны дочерние типы.");

                foreach (var child in children)
                {
                    if (!parent.Children.Contains(child.Id))
                    {
                        parent.Children.Add(child.Id);
                        added.Add(child.Name);
                    }
                }

                if (added.Count == 0)
                    throw new InvalidOperationException("Все указанные типы уже являются дочерними.");
            });
            return ToolResult.Ok($"К типу '{parentTypeName}' добавлены дочерние: {string.Join(", ", added)}. Версия метаданных: {version}.");
        });
    }

    // ===== Атрибуты типов =====

    [McpServerTool, Description(
        "Добавить атрибут (параметр) к типу объекта. Требует учётку администратора сервера. " +
        "attrType: Integer, Double, DateTime, String, Decimal, Numerator, Array, UserState, OrgUnit, ElementBook, Inherited, Boolean, ByteArray.")]
    public Task<string> AddTypeAttribute(
        [Description("Имя типа")] string typeName,
        [Description("Имя атрибута (латиницей, уникальное в типе)")] string name,
        [Description("Отображаемое название атрибута")] string title,
        [Description("Тип атрибута (см. описание инструмента)")] string attrType,
        [Description("Обязательный (true/false)")] bool obligatory = false,
        [Description("Показывать в обозревателе объектов (true/false)")] bool showInTree = false,
        [Description("Порядок отображения (-1 = в конец)")] int displaySortOrder = -1,
        [Description("Конфигурация атрибута (XML/JSON, зависит от типа; пусто = нет)")] string configuration = "",
        [Description("Служебный атрибут (true/false)")] bool isService = false)
    {
        return ToolRunner.RunWriteAsync(_connection, "AddTypeAttribute", $"type={typeName}, attr={name}", async () =>
        {
            var version = await _connection.UpdateMetadataViaAdminAsync(meta =>
            {
                var type = ResolveType(meta, typeName);
                if (type.Attributes.Any(a => a.Name == name))
                    throw new InvalidOperationException($"Атрибут '{name}' уже существует в типе '{typeName}'.");
                if (!Enum.TryParse<MAttrType>(attrType, true, out var mAttrType))
                    throw new InvalidOperationException($"Неизвестный тип атрибута '{attrType}'. Допустимо: {string.Join(", ", Enum.GetNames<MAttrType>())}.");

                type.Attributes.Add(new MAttribute
                {
                    Name = name,
                    Title = title,
                    Type = mAttrType,
                    Obligatory = obligatory,
                    ShowInTree = showInTree,
                    IsService = isService,
                    Configuration = string.IsNullOrEmpty(configuration) ? null : configuration,
                    DisplaySortOrder = displaySortOrder >= 0
                        ? displaySortOrder
                        : (type.Attributes.Count > 0 ? type.Attributes.Max(a => a.DisplaySortOrder) + 1 : 0),
                });
            });
            return ToolResult.Ok($"Атрибут '{name}' добавлен к типу '{typeName}'. Версия метаданных: {version}.");
        });
    }

    [McpServerTool, Description(
        "Изменить атрибут типа. Пустые параметры не меняются. Требует учётку администратора сервера.")]
    public Task<string> UpdateTypeAttribute(
        [Description("Имя типа")] string typeName,
        [Description("Имя атрибута")] string name,
        [Description("Новое отображаемое название (пусто = не менять)")] string title = "",
        [Description("Обязательный: true/false (пусто = не менять)")] string obligatory = "",
        [Description("Показывать в обозревателе: true/false (пусто = не менять)")] string showInTree = "",
        [Description("Порядок отображения (пусто = не менять)")] string displaySortOrder = "",
        [Description("Конфигурация атрибута (пусто = не менять)")] string configuration = "")
    {
        return ToolRunner.RunWriteAsync(_connection, "UpdateTypeAttribute", $"type={typeName}, attr={name}", async () =>
        {
            var changed = new List<string>();
            var version = await _connection.UpdateMetadataViaAdminAsync(meta =>
            {
                var type = ResolveType(meta, typeName);
                var attr = type.Attributes.FirstOrDefault(a => a.Name == name)
                    ?? throw new InvalidOperationException($"Атрибут '{name}' не найден в типе '{typeName}'.");

                if (!string.IsNullOrEmpty(title)) { attr.Title = title; changed.Add("title"); }
                if (!string.IsNullOrEmpty(obligatory)) { attr.Obligatory = ParseBool(obligatory, "obligatory"); changed.Add("obligatory"); }
                if (!string.IsNullOrEmpty(showInTree)) { attr.ShowInTree = ParseBool(showInTree, "showInTree"); changed.Add("showInTree"); }
                if (!string.IsNullOrEmpty(displaySortOrder))
                {
                    if (!int.TryParse(displaySortOrder, out var order))
                        throw new InvalidOperationException($"Некорректное значение displaySortOrder: '{displaySortOrder}'.");
                    attr.DisplaySortOrder = order;
                    changed.Add("displaySortOrder");
                }
                if (!string.IsNullOrEmpty(configuration)) { attr.Configuration = configuration; changed.Add("configuration"); }

                if (changed.Count == 0)
                    throw new InvalidOperationException("Не указаны поля для обновления.");
            });
            return ToolResult.Ok($"Атрибут '{name}' типа '{typeName}' обновлён ({string.Join(", ", changed)}). Версия метаданных: {version}.");
        });
    }

    [McpServerTool, Description("Удалить атрибут типа. Требует учётку администратора сервера.")]
    public Task<string> DeleteTypeAttribute(
        [Description("Имя типа")] string typeName,
        [Description("Имя атрибута")] string name)
    {
        return ToolRunner.RunWriteAsync(_connection, "DeleteTypeAttribute", $"type={typeName}, attr={name}", async () =>
        {
            var version = await _connection.UpdateMetadataViaAdminAsync(meta =>
            {
                var type = ResolveType(meta, typeName);
                var attr = type.Attributes.FirstOrDefault(a => a.Name == name)
                    ?? throw new InvalidOperationException($"Атрибут '{name}' не найден в типе '{typeName}'.");
                type.Attributes.Remove(attr);
            });
            return ToolResult.Ok($"Атрибут '{name}' удалён из типа '{typeName}'. Версия метаданных: {version}.");
        });
    }

    // ===== Состояния и машины состояний =====

    [McpServerTool, Description(
        "Создать пользовательское состояние (UserState). Требует учётку администратора сервера. " +
        "color: None, Color1..Color7.")]
    public Task<string> CreateUserState(
        [Description("Имя состояния (латиницей, уникальное)")] string name,
        [Description("Отображаемое название")] string title,
        [Description("Цвет: None, Color1..Color7")] string color = "None",
        [Description("Завершающее состояние (true/false)")] bool isCompletionState = false)
    {
        return ToolRunner.RunWriteAsync(_connection, "CreateUserState", $"name={name}", async () =>
        {
            var stateId = Guid.NewGuid();
            var version = await _connection.UpdateMetadataViaAdminAsync(meta =>
            {
                if (meta.UserStates.Any(s => s.Name == name))
                    throw new InvalidOperationException($"Состояние '{name}' уже существует.");
                if (!Enum.TryParse<UserStateColors>(color, true, out var stateColor))
                    throw new InvalidOperationException($"Неизвестный цвет '{color}'. Допустимо: {string.Join(", ", Enum.GetNames<UserStateColors>())}.");

                meta.UserStates.Add(new MUserState
                {
                    Id = stateId,
                    Name = name,
                    Title = title,
                    Color = stateColor,
                    IsCompletionState = isCompletionState,
                });
            });
            return ToolResult.Ok($"Состояние '{name}' создано (ID: {stateId}). Версия метаданных: {version}.");
        });
    }

    [McpServerTool, Description(
        "Изменить пользовательское состояние (UserState) по имени. Пустые параметры не меняются. " +
        "Требует учётку администратора сервера.")]
    public Task<string> UpdateUserState(
        [Description("Имя состояния")] string name,
        [Description("Новое отображаемое название (пусто = не менять)")] string title = "",
        [Description("Цвет: None, Color1..Color7 (пусто = не менять)")] string color = "",
        [Description("Завершающее состояние: true/false (пусто = не менять)")] string isCompletionState = "")
    {
        return ToolRunner.RunWriteAsync(_connection, "UpdateUserState", $"name={name}", async () =>
        {
            var changed = new List<string>();
            var version = await _connection.UpdateMetadataViaAdminAsync(meta =>
            {
                var state = meta.UserStates.FirstOrDefault(s => s.Name == name)
                    ?? throw new InvalidOperationException($"Состояние '{name}' не найдено.");
                if (state.IsSystemState)
                    throw new InvalidOperationException($"Состояние '{name}' — системное, изменять нельзя.");

                if (!string.IsNullOrEmpty(title)) { state.Title = title; changed.Add("title"); }
                if (!string.IsNullOrEmpty(color))
                {
                    if (!Enum.TryParse<UserStateColors>(color, true, out var stateColor))
                        throw new InvalidOperationException($"Неизвестный цвет '{color}'. Допустимо: {string.Join(", ", Enum.GetNames<UserStateColors>())}.");
                    state.Color = stateColor;
                    changed.Add("color");
                }
                if (!string.IsNullOrEmpty(isCompletionState))
                {
                    state.IsCompletionState = ParseBool(isCompletionState, "isCompletionState");
                    changed.Add("isCompletionState");
                }

                if (changed.Count == 0)
                    throw new InvalidOperationException("Не указаны поля для обновления.");
            });
            return ToolResult.Ok($"Состояние '{name}' обновлено ({string.Join(", ", changed)}). Версия метаданных: {version}.");
        });
    }

    [McpServerTool, Description(
        "Удалить пользовательское состояние (пометить IsDeleted). Отклоняется, если состояние используется " +
        "в машинах состояний. Требует учётку администратора сервера.")]
    public Task<string> DeleteUserState(
        [Description("Имя состояния")] string name)
    {
        return ToolRunner.RunWriteAsync(_connection, "DeleteUserState", $"name={name}", async () =>
        {
            var version = await _connection.UpdateMetadataViaAdminAsync(meta =>
            {
                var state = meta.UserStates.FirstOrDefault(s => s.Name == name)
                    ?? throw new InvalidOperationException($"Состояние '{name}' не найдено.");
                if (state.IsSystemState)
                    throw new InvalidOperationException($"Состояние '{name}' — системное, удалять нельзя.");

                var usedIn = meta.StateMachines
                    .Where(m => m.StateTransitions != null && (
                        m.StateTransitions.Keys.Contains(state.Id) ||
                        m.StateTransitions.Values.Any(ts => ts.Any(t => t.StateTo == state.Id))))
                    .Select(m => m.Title)
                    .ToList();
                if (usedIn.Count > 0)
                    throw new InvalidOperationException(
                        $"Состояние '{name}' используется в машинах состояний: {string.Join(", ", usedIn)}. Сначала уберите его из переходов.");

                state.IsDeleted = true;
            });
            return ToolResult.Ok($"Состояние '{name}' помечено удалённым. Версия метаданных: {version}.");
        });
    }

    [McpServerTool, Description(
        "Удалить машину состояний. Отклоняется, если машина привязана к типам. Требует учётку администратора сервера.")]
    public Task<string> DeleteStateMachine(
        [Description("ID (Guid) или название машины состояний")] string machine)
    {
        return ToolRunner.RunWriteAsync(_connection, "DeleteStateMachine", $"machine={machine}", async () =>
        {
            var version = await _connection.UpdateMetadataViaAdminAsync(meta =>
            {
                var m = (Guid.TryParse(machine, out var id)
                        ? meta.StateMachines.FirstOrDefault(x => x.Id == id)
                        : meta.StateMachines.FirstOrDefault(x => x.Title == machine))
                    ?? throw new InvalidOperationException($"Машина состояний '{machine}' не найдена.");

                var usedBy = meta.Types.Where(t => t.StateMachineId == m.Id && !t.IsDeleted).Select(t => t.Name).ToList();
                if (usedBy.Count > 0)
                    throw new InvalidOperationException(
                        $"Машина состояний '{machine}' привязана к типам: {string.Join(", ", usedBy)}. Сначала отвяжите её (UpdateType stateMachineId=none).");

                meta.StateMachines.Remove(m);
            });
            return ToolResult.Ok($"Машина состояний '{machine}' удалена. Версия метаданных: {version}.");
        });
    }

    [McpServerTool, Description(
        "Создать машину состояний (StateMachine). Требует учётку администратора сервера. " +
        "transitionsJson — JSON вида {\"<имя или Guid исходного состояния>\": [{\"stateTo\": \"<имя или Guid>\", \"displayName\": \"...\"}]}.")]
    public Task<string> CreateStateMachine(
        [Description("Название машины состояний")] string title,
        [Description("Переходы (JSON, см. описание; пусто = без переходов)")] string transitionsJson = "")
    {
        return ToolRunner.RunWriteAsync(_connection, "CreateStateMachine", $"title={title}", async () =>
        {
            var machineId = Guid.NewGuid();
            var version = await _connection.UpdateMetadataViaAdminAsync(meta =>
            {
                if (meta.StateMachines.Any(m => m.Title == title))
                    throw new InvalidOperationException($"Машина состояний '{title}' уже существует.");

                meta.StateMachines.Add(new MUserStateMachine
                {
                    Id = machineId,
                    Title = title,
                    StateTransitions = ParseTransitions(meta, transitionsJson),
                });
            });
            return ToolResult.Ok($"Машина состояний '{title}' создана (ID: {machineId}). Версия метаданных: {version}.");
        });
    }

    [McpServerTool, Description(
        "Изменить машину состояний: название и/или полная замена переходов. " +
        "Пустые параметры не меняются. Требует учётку администратора сервера.")]
    public Task<string> UpdateStateMachine(
        [Description("ID (Guid) или название машины состояний")] string machine,
        [Description("Новое название (пусто = не менять)")] string title = "",
        [Description("Новые переходы (JSON, ПОЛНОСТЬЮ заменяют существующие; пусто = не менять)")] string transitionsJson = "")
    {
        return ToolRunner.RunWriteAsync(_connection, "UpdateStateMachine", $"machine={machine}", async () =>
        {
            var changed = new List<string>();
            var version = await _connection.UpdateMetadataViaAdminAsync(meta =>
            {
                var m = (Guid.TryParse(machine, out var id)
                        ? meta.StateMachines.FirstOrDefault(x => x.Id == id)
                        : meta.StateMachines.FirstOrDefault(x => x.Title == machine))
                    ?? throw new InvalidOperationException($"Машина состояний '{machine}' не найдена.");

                if (!string.IsNullOrEmpty(title)) { m.Title = title; changed.Add("title"); }
                if (!string.IsNullOrEmpty(transitionsJson))
                {
                    m.StateTransitions = ParseTransitions(meta, transitionsJson);
                    changed.Add("transitions");
                }

                if (changed.Count == 0)
                    throw new InvalidOperationException("Не указаны поля для обновления.");
            });
            return ToolResult.Ok($"Машина состояний '{machine}' обновлена ({string.Join(", ", changed)}). Версия метаданных: {version}.");
        });
    }

    // ===== Вспомогательные =====

    private static bool ParseBool(string value, string paramName)
    {
        if (value.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
        if (value.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
        throw new InvalidOperationException($"Некорректное значение {paramName}: '{value}' (ожидается true/false).");
    }

    private static MType ResolveType(DMetadata meta, string name)
    {
        return meta.Types.FirstOrDefault(t => t.Name == name)
            ?? throw new InvalidOperationException($"Тип '{name}' не найден.");
    }

    private static List<MType> ResolveTypes(DMetadata meta, string commaSeparatedNames)
    {
        var result = new List<MType>();
        if (string.IsNullOrWhiteSpace(commaSeparatedNames))
            return result;
        foreach (var name in commaSeparatedNames.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            result.Add(ResolveType(meta, name));
        return result;
    }

    private static Guid ResolveStateId(DMetadata meta, string nameOrId)
    {
        if (Guid.TryParse(nameOrId, out var id))
        {
            if (meta.UserStates.All(s => s.Id != id))
                throw new InvalidOperationException($"Состояние {id} не найдено.");
            return id;
        }
        var state = meta.UserStates.FirstOrDefault(s => s.Name == nameOrId)
            ?? throw new InvalidOperationException($"Состояние '{nameOrId}' не найдено.");
        return state.Id;
    }

    private static StateTransitionsCollection ParseTransitions(DMetadata meta, string transitionsJson)
    {
        var collection = new StateTransitionsCollection();
        if (string.IsNullOrWhiteSpace(transitionsJson))
            return collection;

        using var doc = JsonDocument.Parse(transitionsJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("transitionsJson должен быть JSON-объектом: {\"<состояние>\": [{\"stateTo\": ...}]}.");

        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            var fromId = ResolveStateId(meta, prop.Name);
            if (prop.Value.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException($"Переходы для '{prop.Name}' должны быть массивом.");

            var transitions = new List<MTransition>();
            foreach (var el in prop.Value.EnumerateArray())
            {
                var stateTo = el.TryGetProperty("stateTo", out var toEl) ? toEl.GetString() : null;
                if (string.IsNullOrEmpty(stateTo))
                    throw new InvalidOperationException($"У перехода из '{prop.Name}' не указан stateTo.");

                transitions.Add(new MTransition
                {
                    StateTo = ResolveStateId(meta, stateTo),
                    DisplayName = el.TryGetProperty("displayName", out var dn) ? dn.GetString() ?? "" : "",
                });
            }
            collection[fromId] = transitions.ToArray();
        }
        return collection;
    }
}
