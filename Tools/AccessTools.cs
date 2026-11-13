using Ascon.Pilot.DataClasses;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text;

namespace PilotMCP;

[McpServerToolType]
public class AccessTools
{
    private readonly PilotConnection _connection;
    public AccessTools(PilotConnection connection) => _connection = connection;

    // Шкала прав с Pilot 26.20: Edit разбит на EditAttributes/EditFiles/Delete
    private const string AccessLevelsHelp =
        "Флаги: None, Create, EditAttributes, View, Freeze, Approve, Share, EditFiles, Delete. " +
        "Комбинации: Edit = EditAttributes+EditFiles+Delete, ViewCreate, ViewEdit = View+Create+Edit, " +
        "ViewEditApprove, Full. Agreement и ViewEditAgrement — устаревшие синонимы Approve и ViewEditApprove " +
        "(в выводе могут печататься под старыми именами).";

    [McpServerTool, Description(
        "Проверить права доступа пользователя к объекту. " +
        AccessLevelsHelp)]
    public Task<string> CheckAccess(
        [Description("GUID объекта")] string objectId,
        [Description("ID пользователя (personId). Если 0, проверяет текущего.")] int personId = 0)
    {
        return ToolRunner.RunAsync(_connection, "CheckAccess", $"obj={objectId}, person={personId}", async () =>
        {
            if (!Guid.TryParse(objectId, out var guid))
                return ToolResult.Error($"Некорректный GUID: {objectId}");

            if (personId == 0) personId = _connection.CurrentPersonId;

            var level = await _connection.ServerApi.CalcAccessByPersonAsync(guid, personId);
            return ToolResult.Ok($"Права пользователя #{personId} на объект {objectId}: {level}");
        });
    }

    [McpServerTool, Description(
        "Проверить права доступа организационной единицы к объекту.")]
    public Task<string> CheckAccessByUnit(
        [Description("GUID объекта")] string objectId,
        [Description("ID организационной единицы (unitId)")] int unitId)
    {
        return ToolRunner.RunAsync(_connection, "CheckAccessByUnit", $"obj={objectId}, unit={unitId}", async () =>
        {
            if (!Guid.TryParse(objectId, out var guid))
                return ToolResult.Error($"Некорректный GUID: {objectId}");

            var level = await _connection.ServerApi.CalcAccessByUnitAsync(guid, unitId);
            return ToolResult.Ok($"Права оргединицы #{unitId} на объект {objectId}: {level}");
        });
    }

    [McpServerTool, Description(
        "Получить список подписчиков объекта (personId пользователей, подписанных на уведомления).")]
    public Task<string> GetSubscribers(
        [Description("GUID объекта")] string objectId)
    {
        return ToolRunner.RunAsync(_connection, "GetSubscribers", $"obj={objectId}", async () =>
        {
            if (!Guid.TryParse(objectId, out var guid))
                return ToolResult.Error($"Некорректный GUID: {objectId}");

            var subscribers = await _connection.ServerApi.GetSubscribersAsync(guid);

            if (subscribers == null || !subscribers.Any())
                return ToolResult.Ok("Подписчиков нет.");

            var sb = new StringBuilder();
            sb.AppendLine($"Подписчики объекта {objectId} ({subscribers.Count}):");
            foreach (var personId in subscribers)
                sb.AppendLine($"  personId: {personId}");

            return ToolResult.Ok(sb.ToString());
        });
    }

    [McpServerTool, Description(
        "Получить записи прав доступа объекта (кому и какие права назначены).")]
    public Task<string> GetAccessRecords(
        [Description("GUID объекта")] string objectId)
    {
        return ToolRunner.RunAsync(_connection, "GetAccessRecords", $"obj={objectId}", async () =>
        {
            if (!Guid.TryParse(objectId, out var guid))
                return ToolResult.Error($"Некорректный GUID: {objectId}");

            var records = (await _connection.ServerApi.LoadAccessRecordsAsync(guid)).ToList();

            if (records.Count == 0)
                return ToolResult.Ok("Записей прав доступа нет (используются унаследованные).");

            var units = await _connection.ServerApi.LoadOrganisationUnitsAsync();
            var unitMap = units.ToDictionary(u => u.Id, u => u.Title ?? $"#{u.Id}");

            var sb = new StringBuilder();
            sb.AppendLine($"Права доступа объекта {objectId} ({records.Count} записей):");
            foreach (var r in records)
            {
                var unitName = unitMap.GetValueOrDefault(r.OrgUnitId, $"OrgUnit#{r.OrgUnitId}");
                sb.AppendLine($"  {unitName}: {r.Access}" +
                    (r.InheritanceSource != Guid.Empty ? $" (унаследовано от {r.InheritanceSource})" : ""));
            }

            return ToolResult.Ok(sb.ToString());
        });
    }

    [McpServerTool, Description(
        "Добавить запись прав доступа к объекту. " + AccessLevelsHelp)]
    public Task<string> AddAccessRecord(
        [Description("GUID объекта")] string objectId,
        [Description("ID позиции (orgUnitId)")] int positionId,
        [Description("Уровень доступа: имя флага или несколько через запятую, например 'View,EditAttributes'")] string accessLevel,
        [Description("Наследование: None, InheritUntilSecret, InheritWholeSubtree")] string inheritance = "InheritWholeSubtree",
        [Description("Тип: Allow или Deny")] string accessType = "Allow")
    {
        return ToolRunner.RunWriteAsync(_connection, "AddAccessRecord", $"obj={objectId}, pos={positionId}, level={accessLevel}", async () =>
        {
            if (!Guid.TryParse(objectId, out var guid))
                return ToolResult.Error($"Некорректный GUID: {objectId}");
            if (!Enum.TryParse<AccessLevel>(accessLevel, ignoreCase: true, out var level))
                return ToolResult.Error($"Неизвестный уровень доступа: {accessLevel}");
            if (!Enum.TryParse<AccessInheritance>(inheritance, ignoreCase: true, out var inh))
                return ToolResult.Error($"Неизвестный тип наследования: {inheritance}");
            if (!Enum.TryParse<AccessType>(accessType, ignoreCase: true, out var aType))
                return ToolResult.Error($"Неизвестный тип доступа: {accessType}");

            var modifier = _connection.CreateModifier();
            modifier.EditObject(guid).AddAccessRecord(positionId, level, DateTime.MaxValue, inh, aType);
            if (modifier.AnyChanges()) _connection.ApplyAndWait(() => modifier.Apply(null));
            return ToolResult.Ok($"Право доступа добавлено: позиция #{positionId}, уровень {accessLevel}.");
        });
    }

    [McpServerTool, Description("Сделать объект публичным (удалить все ограничения доступа).")]
    public Task<string> RemoveAllAccessRecords(
        [Description("GUID объекта")] string objectId)
    {
        return ToolRunner.RunWriteAsync(_connection, "RemoveAllAccessRecords", $"obj={objectId}", async () =>
        {
            if (!Guid.TryParse(objectId, out var guid))
                return ToolResult.Error($"Некорректный GUID: {objectId}");

            var modifier = _connection.CreateModifier();
            modifier.EditObject(guid).RemoveAccessRecords(_ => true);
            if (modifier.AnyChanges()) _connection.ApplyAndWait(() => modifier.Apply(null));
            return ToolResult.Ok($"Все записи доступа удалены с объекта {objectId}.");
        });
    }
}
