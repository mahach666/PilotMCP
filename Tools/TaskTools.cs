using Ascon.Pilot.DataModifier.NextTasks;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text;

namespace PilotMCP;

[McpServerToolType]
public class TaskTools
{
    private readonly PilotConnection _connection;
    public TaskTools(PilotConnection connection) => _connection = connection;

    /// <summary>0 → первая позиция текущего пользователя (если есть).</summary>
    private int ResolveInitiator(int positionId)
    {
        if (positionId > 0) return positionId;
        var positions = _connection.DatabaseInfo.Person?.Positions;
        return positions is { Count: > 0 } ? positions[0] : 0;
    }

    [McpServerTool, Description(
        "Создать задачу в Pilot. Тип задачи всегда начинается с 'task_' (получите через GetTypes). " +
        "Задача привязывается к объекту через связь TaskAttachments (не через parentId). " +
        "attachmentIds — GUID существующих объектов, к которым относится задача. " +
        "deadline — дата yyyy-MM-dd, срок наступает в конце этого дня (23:59:59 по местному времени).")]
    public Task<string> CreateTask(
        [Description("Системное имя типа задачи (начинается с 'task_', например 'task_simple').")] string typeName,
        [Description("GUID объектов-вложений через запятую (к каким объектам привязана задача).")] string attachmentIds = "",
        [Description("ID позиции инициатора (если 0 — первая позиция текущего пользователя).")] int initiatorPositionId = 0,
        [Description("ID позиции исполнителя.")] int executorPositionId = 0,
        [Description("Дедлайн (формат: yyyy-MM-dd).")] string deadline = "",
        [Description("Атрибуты в формате JSON: {\"name\":\"value\"}.")] string attributes = "")
    {
        return ToolRunner.RunWriteAsync(_connection, "CreateTask", $"type={typeName}", async () =>
        {
            var type = _connection.Metadata.Types.FirstOrDefault(t =>
                t.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase));
            if (type == null)
                return ToolResult.Error($"Тип '{typeName}' не найден. Типы задач начинаются с 'task_'.");
            if (!type.Name.StartsWith(Ascon.Pilot.DataClasses.SystemTypes.TASK_PREFIX, StringComparison.Ordinal))
                return ToolResult.Error($"Тип '{type.Name}' не является типом задачи (имя должно начинаться с 'task_').");

            // всё проверяем до построения изменений: ничего не создаём из частично некорректных аргументов
            DateTime deadlineUtc = default;
            var hasDeadline = !string.IsNullOrWhiteSpace(deadline);
            if (hasDeadline && !MessageHelpers.TryParseDeadline(deadline, out deadlineUtc))
                return ToolResult.Error($"Некорректный дедлайн: '{deadline}'. Формат: yyyy-MM-dd.");

            if (!MessageHelpers.TryParseGuids(attachmentIds, out var guids, out var badGuid))
                return ToolResult.Error($"Некорректный GUID во вложениях: '{badGuid}'.");
            if (guids.Count > 0)
            {
                var found = (await _connection.ServerApi.GetObjectsAsync(guids.ToArray())).Select(o => o.Id).ToHashSet();
                var missing = guids.Where(g => !found.Contains(g)).ToList();
                if (missing.Count > 0)
                    return ToolResult.Error($"Объекты-вложения не найдены: {string.Join(", ", missing)}.");
            }

            Dictionary<string, Ascon.Pilot.DataClasses.DValue>? attrs;
            try { attrs = Helpers.ParseAttributes(attributes); }
            catch (ArgumentException ex) { return ToolResult.Error($"Некорректные атрибуты: {ex.Message}"); }

            var taskModifier = new TaskModifier(_connection.Backend);
            var taskId = Guid.NewGuid();

            var builder = taskModifier.CreateTask(taskId, type, Guid.Empty);

            initiatorPositionId = ResolveInitiator(initiatorPositionId);
            if (initiatorPositionId > 0)
                builder.SetInitiator(initiatorPositionId);

            if (executorPositionId > 0)
                builder.SetExecutor(executorPositionId);
            if (hasDeadline)
                builder.SetAttribute("deadlineDate", deadlineUtc);

            if (guids.Count > 0)
                builder.SetAttachments(guids);

            if (attrs != null)
            {
                foreach (var kvp in attrs)
                    builder.SetAttribute(kvp.Key, kvp.Value);
            }

            _connection.ApplyAndWait(() => taskModifier.Apply());

            var sb = new StringBuilder();
            sb.AppendLine($"Задача создана. ID: {taskId}");
            sb.AppendLine($"Тип: {typeName}");
            if (executorPositionId > 0) sb.AppendLine($"Исполнитель: positionId={executorPositionId}");
            if (hasDeadline) sb.AppendLine($"Дедлайн: {deadline} 23:59:59 (местное время)");
            if (guids.Count > 0) sb.AppendLine($"Привязана к: {string.Join(", ", guids)}");

            return ToolResult.Ok(sb.ToString());
        });
    }

    [McpServerTool, Description(
        "Создать пустой workflow (процесс) без стадий: стадии и задачи этот инструмент не добавляет. " +
        "Тип workflow начинается с 'workflow_'.")]
    public Task<string> CreateWorkflow(
        [Description("GUID родительского объекта")] string parentId,
        [Description("Системное имя типа workflow")] string typeName,
        [Description("ID позиции инициатора (если 0 — первая позиция текущего пользователя)")] int initiatorPositionId = 0,
        [Description("Атрибуты в формате JSON: {\"name\":\"value\"}")] string attributes = "")
    {
        return ToolRunner.RunWriteAsync(_connection, "CreateWorkflow", $"parent={parentId}, type={typeName}", async () =>
        {
            if (!Guid.TryParse(parentId, out var parentGuid))
                return ToolResult.Error($"Некорректный GUID: {parentId}");

            var type = _connection.Metadata.Types.FirstOrDefault(t =>
                t.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase));
            if (type == null)
                return ToolResult.Error($"Тип '{typeName}' не найден.");
            if (!type.Name.StartsWith(Ascon.Pilot.DataClasses.SystemTypes.WORKFLOW_PREFIX, StringComparison.Ordinal))
                return ToolResult.Error($"Тип '{type.Name}' не является типом workflow (имя должно начинаться с 'workflow_').");

            Dictionary<string, Ascon.Pilot.DataClasses.DValue>? attrs;
            try { attrs = Helpers.ParseAttributes(attributes); }
            catch (ArgumentException ex) { return ToolResult.Error($"Некорректные атрибуты: {ex.Message}"); }

            var taskModifier = new TaskModifier(_connection.Backend);
            var workflowId = Guid.NewGuid();
            var builder = taskModifier.CreateWorkflow(workflowId, type, parentGuid);

            initiatorPositionId = ResolveInitiator(initiatorPositionId);
            if (initiatorPositionId > 0)
                builder.SetInitiator(initiatorPositionId);

            if (attrs != null)
            {
                var objBuilder = taskModifier.EditObject(workflowId);
                foreach (var kvp in attrs)
                    objBuilder.SetAttribute(kvp.Key, kvp.Value);
            }

            _connection.ApplyAndWait(() => taskModifier.Apply());

            return ToolResult.Ok($"Workflow создан: {workflowId}\nТип: {typeName}");
        });
    }
}
