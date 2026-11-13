using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text;

namespace PilotMCP;

[McpServerToolType]
public class PeopleTools
{
    private readonly PilotConnection _connection;
    public PeopleTools(PilotConnection connection) => _connection = connection;

    [McpServerTool, Description(
        "Получить информацию о конкретных пользователях по их ID.")]
    public Task<string> GetPeopleByIds(
        [Description("ID пользователей через запятую")] string personIds)
    {
        return ToolRunner.RunAsync(_connection, "GetPeopleByIds", $"ids={personIds}", async () =>
        {
            var ids = personIds.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(s => int.TryParse(s, out var id) ? id : -1)
                .Where(id => id >= 0)
                .ToArray();

            if (ids.Length == 0)
                return ToolResult.Error("Не указаны корректные ID.");

            var people = await _connection.ServerApi.LoadPeopleByIdsAsync(ids);

            var sb = new StringBuilder();
            sb.AppendLine($"Пользователи ({people.Count}):");
            foreach (var p in people)
            {
                sb.AppendLine($"  #{p.Id}: {p.FullName}");
                sb.AppendLine($"    Login: {p.Login}, Email: {p.Email ?? ""}, Phone: {p.Phone ?? ""}");
                sb.AppendLine($"    AccountState: {p.AccountState}, IsAdmin: {p.IsAdmin}, IsDeleted: {p.IsDeleted}, IsInactive: {p.IsInactive}");
                if (!string.IsNullOrEmpty(p.Comment)) sb.AppendLine($"    Comment: {p.Comment}");
                sb.AppendLine($"    Позиции (orgUnitIds): {string.Join(", ", p.Positions.Select(pos => $"#{pos}"))}");
                if (p.AllOrgUnits.Any()) sb.AppendLine($"    Все оргединицы: {string.Join(", ", p.AllOrgUnits.Select(id => $"#{id}"))}");
                if (p.Groups.Any()) sb.AppendLine($"    Группы: {string.Join(", ", p.Groups.Select(id => $"#{id}"))}");
            }

            return ToolResult.Ok(sb.ToString());
        });
    }

    [McpServerTool, Description(
        "Получить организационные единицы по ID.")]
    public Task<string> GetOrgUnitsByIds(
        [Description("ID оргединиц через запятую")] string unitIds)
    {
        return ToolRunner.RunAsync(_connection, "GetOrgUnitsByIds", $"ids={unitIds}", async () =>
        {
            var ids = unitIds.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(s => int.TryParse(s, out var id) ? id : -1)
                .Where(id => id >= 0)
                .ToArray();

            if (ids.Length == 0)
                return ToolResult.Error("Не указаны корректные ID.");

            var units = await _connection.ServerApi.LoadOrganisationUnitsByIdsAsync(ids);

            var sb = new StringBuilder();
            sb.AppendLine($"Организационные единицы ({units.Count}):");
            foreach (var u in units)
            {
                sb.AppendLine($"  #{u.Id}: {u.Title}");
                sb.AppendLine($"    Kind: {u.Kind}, ParentId: #{u.ParentId}, Person (personId): #{u.Person}");
                sb.AppendLine($"    IsDeleted: {u.IsDeleted}, IsCanceled: {u.IsCanceled}, IsBoss: {u.IsBoss}");
                if (u.VicePersons.Any()) sb.AppendLine($"    Заместители: {string.Join(", ", u.VicePersons.Select(v => $"#{v}"))}");
                if (u.GroupPersons.Any()) sb.AppendLine($"    Члены группы: {string.Join(", ", u.GroupPersons.Select(g => $"#{g}"))}");
                if (u.PermanentVicePersons.Any()) sb.AppendLine($"    Постоянные замы: {string.Join(", ", u.PermanentVicePersons.Select(p => $"#{p}"))}");
            }

            return ToolResult.Ok(sb.ToString());
        });
    }

    [McpServerTool, Description(
        "Узнать кто занимает указанную позицию (по positionId из организационной структуры).")]
    public Task<string> GetPersonOnPosition(
        [Description("ID позиции (из GetOrganisationUnits)")] int positionId)
    {
        return ToolRunner.RunAsync(_connection, "GetPersonOnPosition", $"pos={positionId}", async () =>
        {
            var units = await _connection.ServerApi.LoadOrganisationUnitsByIdsAsync(new[] { positionId });
            if (units == null || units.Count == 0)
                return ToolResult.Error($"Позиция #{positionId} не найдена.");

            var unit = units[0];
            if (unit.Person <= 0)
                return ToolResult.Ok($"Позиция #{positionId} ({unit.Title}): вакантна.");

            var people = await _connection.ServerApi.LoadPeopleByIdsAsync(new[] { unit.Person });
            var person = people.Count > 0 ? people[0] : null;

            var sb = new StringBuilder();
            sb.AppendLine($"Позиция #{positionId}: {unit.Title}");
            if (person != null)
            {
                sb.AppendLine($"  Сотрудник: {person.FullName} (#{person.Id})");
                sb.AppendLine($"  Логин: {person.Login}");
                if (!string.IsNullOrEmpty(person.Email)) sb.AppendLine($"  Email: {person.Email}");
                if (!string.IsNullOrEmpty(person.Phone)) sb.AppendLine($"  Телефон: {person.Phone}");
            }
            else
            {
                sb.AppendLine($"  Сотрудник: #{unit.Person} (не загружен)");
            }

            if (unit.VicePersons?.Count > 0)
                sb.AppendLine($"  Заместители: {string.Join(", ", unit.VicePersons.Select(v => $"#{v}"))}");
            if (unit.PermanentVicePersons?.Count > 0)
                sb.AppendLine($"  Постоянные заместители: {string.Join(", ", unit.PermanentVicePersons.Select(v => $"#{v}"))}");

            return ToolResult.Ok(sb.ToString());
        });
    }
}
