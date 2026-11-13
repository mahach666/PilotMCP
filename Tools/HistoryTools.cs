using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text;

namespace PilotMCP;

[McpServerToolType]
public class HistoryTools
{
    private readonly PilotConnection _connection;
    public HistoryTools(PilotConnection connection) => _connection = connection;

    [McpServerTool, Description(
        "Получить историю изменений объекта. Показывает кто, когда и что менял. Поддерживает пагинацию.")]
    public Task<string> GetHistory(
        [Description("GUID объекта")] string objectId,
        [Description("Количество записей (по умолчанию 50)")] int count = 50,
        [Description("Пропустить первых N записей (для пагинации)")] int skip = 0)
    {
        return ToolRunner.RunAsync(_connection, "GetHistory", $"obj={objectId}, count={count}, skip={skip}", async () =>
        {
            if (!Guid.TryParse(objectId, out var guid))
                return ToolResult.Error($"Некорректный GUID: {objectId}");

            var objects = await _connection.ServerApi.GetObjectsAsync(new[] { guid });
            if (objects == null || objects.Count == 0)
                return ToolResult.Error($"Объект {objectId} не найден.");

            var historyIds = objects[0].HistoryItems;
            if (historyIds == null || historyIds.Count == 0)
                return ToolResult.Ok("История изменений пуста (нет HistoryItems у объекта).");

            var items = await _connection.ServerApi.GetHistoryItemsAsync(historyIds.ToArray());
            var all = items.ToList();

            if (all.Count == 0)
                return ToolResult.Ok("История изменений пуста.");

            var sorted = all.OrderByDescending(i => i.Created).ToList();
            var page = sorted.Skip(skip).Take(count).ToList();

            var people = await _connection.ServerApi.LoadPeopleAsync();
            var personMap = people.ToDictionary(p => p.Id, p => p.DisplayName ?? p.Login ?? $"#{p.Id}");

            var sb = new StringBuilder();
            sb.AppendLine($"История объекта {objectId} ({page.Count} из {all.Count}, skip={skip}):");
            sb.AppendLine();
            foreach (var item in page)
            {
                var who = personMap.GetValueOrDefault(item.CreatorId, $"#{item.CreatorId}");
                sb.AppendLine($"  [{item.Created:yyyy-MM-dd HH:mm}] {who}: {item.Reason}");
            }
            if (skip + page.Count < all.Count)
                sb.AppendLine($"  ... ещё {all.Count - skip - page.Count} записей (используйте skip={skip + page.Count})");

            return ToolResult.Ok(sb.ToString());
        });
    }

    private const long MaxChangesetRange = 1000;

    [McpServerTool, Description(
        "Получить набор изменений (changesets) за период: номер, идентификатор, число изменённых объектов и их GUID. " +
        "Диапазон не более 1000 номеров (last-first < 1000).")]
    public Task<string> GetChangesets(
        [Description("Номер первого changeset")] long first,
        [Description("Номер последнего changeset")] long last)
    {
        return ToolRunner.RunAsync(_connection, "GetChangesets", $"first={first}, last={last}", async () =>
        {
            if (first < 0 || last < first)
                return ToolResult.Error("Некорректный диапазон: нужно 0 <= first <= last.");
            if (last - first >= MaxChangesetRange)
                return ToolResult.Error($"Диапазон слишком большой: не более {MaxChangesetRange} номеров.");

            var changesets = await _connection.ServerApi.GetChangesetsAsync(first, last);

            var (path, count) = TempFiles.WriteJsonl(changesets, cs => new Dictionary<string, object>
            {
                ["id"] = cs.Id,
                ["identity"] = cs.Identity.ToString(),
                ["changed"] = cs.Changed?.Count ?? 0,
                ["changedIds"] = cs.Changed?.Select(g => g.ToString()).ToArray() ?? Array.Empty<string>()
            }, "changesets");

            return ToolResult.File(path, count, new[] { "id", "identity", "changed", "changedIds" },
                $"Получено {count} changesets ({first}..{last})");
        });
    }
}
