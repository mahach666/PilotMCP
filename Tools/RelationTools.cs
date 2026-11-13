using Ascon.Pilot.DataClasses;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace PilotMCP;

[McpServerToolType]
public class RelationTools
{
    private readonly PilotConnection _connection;
    public RelationTools(PilotConnection connection) => _connection = connection;

    private const string AllowedTypes =
        "SourceFiles, TaskInitiatorAttachments, TaskExecutorAttachments, MessageAttachments, Custom, TaskAttachments";

    // Enum.TryParse принимает и числа ("99"), и служебный Publish — оставляем только перечисленные в описании типы.
    internal static bool TryParseLinkType(string value, out RelationType type)
    {
        type = default;
        return Enum.TryParse(value, ignoreCase: true, out type)
               && Enum.IsDefined(type)
               && type != RelationType.Publish
               && !int.TryParse(value, out _);
    }

    [McpServerTool, Description(
        "Создать двустороннюю связь между двумя объектами Pilot. " +
        "Типы связей: SourceFiles, TaskInitiatorAttachments, TaskExecutorAttachments, MessageAttachments, Custom, TaskAttachments. " +
        "Используйте Custom для произвольных связей. name — необязательное имя связи. " +
        "Если такая связь (тот же объект, тип и имя) уже есть — ничего не создаётся.")]
    public Task<string> CreateLink(
        [Description("GUID первого объекта")] string objectId1,
        [Description("GUID второго объекта")] string objectId2,
        [Description("Тип связи: SourceFiles, TaskInitiatorAttachments, TaskExecutorAttachments, MessageAttachments, Custom, TaskAttachments")] string relationType,
        [Description("Имя связи (необязательно, например для Custom)")] string name = "")
    {
        return ToolRunner.RunWriteAsync(_connection, "CreateLink",
            $"obj1={objectId1}, obj2={objectId2}, type={relationType}, name={name}", async () =>
        {
            if (!Guid.TryParse(objectId1, out var guid1))
                return ToolResult.Error("Некорректный GUID первого объекта.");
            if (!Guid.TryParse(objectId2, out var guid2))
                return ToolResult.Error("Некорректный GUID второго объекта.");
            if (!TryParseLinkType(relationType, out var relType))
                return ToolResult.Error($"Неизвестный тип связи: {relationType}. Допустимые: {AllowedTypes}.");

            // AddRelation не проверяет дубликаты (каждый вызов создаёт связь с новым Id), поэтому проверяем сами.
            var existing = await _connection.ServerApi.GetObjectsAsync(new[] { guid1, guid2 });
            var first = existing?.FirstOrDefault(o => o.Id == guid1);
            if (first == null)
                return ToolResult.Error($"Объект {objectId1} не найден.");
            if (existing!.All(o => o.Id != guid2))
                return ToolResult.Error($"Объект {objectId2} не найден.");
            if (first.Relations.Any(r => r.TargetId == guid2 && r.Type == relType && (r.Name ?? "") == (name ?? "")))
                return ToolResult.Ok($"Связь уже существует: {objectId1} <-> {objectId2}, тип: {relType}. Ничего не создано.");

            var modifier = _connection.CreateModifier();
            modifier.AddRelation(guid1, guid2, relType, name ?? "");

            if (!modifier.AnyChanges())
                return ToolResult.Error("Нет изменений для применения.");

            var changesetId = _connection.ApplyAndWait(() => modifier.Apply(null));

            var objects = await _connection.ServerApi.GetObjectsAsync(new[] { guid1 });
            if (objects != null && objects.Count > 0)
            {
                var hasRelation = objects[0].Relations.Any(r => r.TargetId == guid2 && r.Type == relType);
                if (!hasRelation)
                    return ToolResult.Error(
                        $"Changeset {changesetId} отправлен, но связь не обнаружена при верификации. " +
                        "Сервер мог отклонить изменение.");
            }

            return ToolResult.Ok(
                $"Связь создана: {objectId1} <-> {objectId2}, тип: {relationType}" +
                (string.IsNullOrEmpty(name) ? "" : $", имя: {name}") +
                $". Changeset: {changesetId}");
        });
    }

    [McpServerTool, Description(
        "Удалить связь между двумя объектами Pilot. " +
        "Если тип связи не указан — удаляются все связи с targetId.")]
    public Task<string> DeleteRelation(
        [Description("GUID исходного объекта")] string sourceId,
        [Description("GUID целевого объекта")] string targetId,
        [Description("Тип связи (необязательно). Если не указан — удаляются все связи с targetId.")] string relationType = "")
    {
        return ToolRunner.RunWriteAsync(_connection, "DeleteRelation",
            $"source={sourceId}, target={targetId}, type={relationType}", async () =>
        {
            if (!Guid.TryParse(sourceId, out var srcGuid))
                return ToolResult.Error("Некорректный GUID исходного объекта.");
            if (!Guid.TryParse(targetId, out var tgtGuid))
                return ToolResult.Error("Некорректный GUID целевого объекта.");

            RelationType? relType = null;
            if (!string.IsNullOrEmpty(relationType))
            {
                if (!Enum.TryParse<RelationType>(relationType, ignoreCase: true, out var parsedType))
                    return ToolResult.Error($"Неизвестный тип связи: {relationType}.");
                relType = parsedType;
            }

            var objects = await _connection.ServerApi.GetObjectsAsync(new[] { srcGuid });
            if (objects == null || objects.Count == 0)
                return ToolResult.Error($"Объект {sourceId} не найден.");

            var relations = objects[0].Relations
                .Where(r => r.TargetId == tgtGuid && (relType == null || r.Type == relType))
                .ToList();

            if (relations.Count == 0)
                return ToolResult.Ok("Связь не найдена — ничего не удалено.");

            var modifier = _connection.CreateModifier();
            var builder = modifier.EditObject(srcGuid);
            foreach (var rel in relations)
                builder.RemoveRelationById(rel.Id);

            if (!modifier.AnyChanges())
                return ToolResult.Error("Нет изменений для применения.");

            var changesetId = _connection.ApplyAndWait(() => modifier.Apply(null));
            return ToolResult.Ok($"Удалено связей: {relations.Count} (между {sourceId} и {targetId}). Changeset: {changesetId}");
        });
    }

    private const int MaxAccessEntries = 20;

    [McpServerTool, Description(
        "Получить объекты с правами доступа. Для каждого объекта выводятся: уровень (level) — уровень доступа, который сервер вернул для текущего пользователя, " +
        "и записи прав в виде #orgUnitId:уровень (первые 20, остальные — счётчиком). Некорректный GUID в списке — ошибка.")]
    public Task<string> GetObjectsWithRights(
        [Description("GUID объектов через запятую")] string objectIds)
    {
        return ToolRunner.RunAsync(_connection, "GetObjectsWithRights", objectIds, async () =>
        {
            var parts = objectIds.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var invalid = parts.Where(id => !Guid.TryParse(id, out var g) || g == Guid.Empty).ToList();
            if (invalid.Count > 0)
                return ToolResult.Error($"Некорректные GUID: {string.Join(", ", invalid)}.");
            var guids = parts.Select(Guid.Parse).ToArray();

            if (guids.Length == 0)
                return ToolResult.Error("Не указаны корректные GUID.");

            var results = await _connection.ServerApi.GetObjectsWithRightsAsync(guids);
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Объекты с правами ({results.Count}):");
            foreach (var (obj, level, subtypes) in results)
            {
                var displayName = Helpers.GetDisplayName(obj, _connection.Metadata);
                sb.AppendLine($"  - {obj.Id}: {displayName}, уровень: {level}, доступ: [{string.Join(", ", obj.Access.Take(MaxAccessEntries).Select(a => $"#{a.OrgUnitId}:{a.Access.AccessLevel}"))}]" +
                              (obj.Access.Count > MaxAccessEntries ? $" ... и ещё {obj.Access.Count - MaxAccessEntries} записей" : ""));
            }

            return ToolResult.Ok(sb.ToString());
        });
    }
}
