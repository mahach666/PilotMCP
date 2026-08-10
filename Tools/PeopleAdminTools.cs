using Ascon.Pilot.Common;
using Ascon.Pilot.DataClasses;
using Ascon.Pilot.Server.Api.Contracts;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace PilotMCP;

/// <summary>
/// Инструменты управления пользователями и оргструктурой через админский API
/// (LoginServerAdministrator) — как в Pilot-myAdmin.
/// Пользователи меняются через ChangePeople(DPersonChangeset), оргструктура — через
/// ChangeOrganisationUnit(DOrganisationUnitChangesetData) с клонами Old/Updated.
/// Пароль передаётся AES-шифрованным (AesEx.EncryptAes) — так же, как myAdmin.
/// </summary>
[McpServerToolType]
public class PeopleAdminTools
{
    private readonly PilotConnection _connection;
    public PeopleAdminTools(PilotConnection connection) => _connection = connection;

    // ===== Пользователи =====

    [McpServerTool, Description(
        "Создать пользователя базы. Требует учётку администратора сервера " +
        "(PILOT_ADMIN_LOGIN/PILOT_ADMIN_PASSWORD). " +
        "Важно: войти в систему новый пользователь сможет только после назначения на должность " +
        "(SetPersonOnPosition) — иначе сервер отвечает 'Person must hold the position'.")]
    public Task<string> CreatePerson(
        [Description("Логин (латиницей, уникальный)")] string login,
        [Description("Отображаемое имя")] string displayName,
        [Description("Пароль")] string password,
        [Description("Администратор базы (true/false)")] bool isAdmin = false,
        [Description("Email (пусто = нет)")] string email = "",
        [Description("Комментарий (пусто = нет)")] string comment = "")
    {
        return ToolRunner.RunWriteAsync(_connection, "CreatePerson", $"login={login}", async () =>
        {
            _connection.EnsureConnected();
            var created = await _connection.WithAdminApiAsync(async admin =>
            {
                var people = await admin.LoadPeopleAsync(_connection.DatabaseName);
                if (people.Any(p => p.Login == login && !p.IsDeleted))
                    throw new InvalidOperationException($"Пользователь с логином '{login}' уже существует.");

                var person = new DPerson
                {
                    Id = people.Count > 0 ? people.Max(p => p.Id) + 1 : 1,
                    Login = login,
                    DisplayName = displayName,
                    IsAdmin = isAdmin,
                    Email = string.IsNullOrEmpty(email) ? null : email,
                    Comment = string.IsNullOrEmpty(comment) ? null : comment,
                    AccountState = AccountStates.Active,
                    CreatedUtc = DateTime.UtcNow,
                };
                var change = new DPersonChangeData(null, person, password.EncryptAes());
                var result = await admin.ChangePeopleAsync(_connection.DatabaseName, new DPersonChangeset(change));
                return result.ChangedPeople.FirstOrDefault() ?? person;
            });
            return ToolResult.Ok($"Пользователь '{login}' создан (ID: {created.Id}).");
        });
    }

    [McpServerTool, Description(
        "Изменить пользователя через админский API: логин, отображаемое имя, пароль, признак администратора, " +
        "признак активности. Пустые параметры не меняются. Требует учётку администратора сервера.")]
    public Task<string> UpdatePersonAdmin(
        [Description("Текущий логин пользователя")] string login,
        [Description("Новый логин (пусто = не менять)")] string newLogin = "",
        [Description("Новое отображаемое имя (пусто = не менять)")] string displayName = "",
        [Description("Новый пароль (пусто = не менять)")] string password = "",
        [Description("Администратор базы: true/false (пусто = не менять)")] string isAdmin = "",
        [Description("Сделать неактивным: true/false (пусто = не менять)")] string isInactive = "")
    {
        return ToolRunner.RunWriteAsync(_connection, "UpdatePersonAdmin", $"login={login}", async () =>
        {
            _connection.EnsureConnected();
            var changed = new List<string>();
            await _connection.WithAdminApiAsync<object?>(async admin =>
            {
                var old = await FindPersonAsync(admin, login);
                var updated = old.Clone();

                if (!string.IsNullOrEmpty(newLogin) && newLogin != login)
                {
                    var people = await admin.LoadPeopleAsync(_connection.DatabaseName);
                    if (people.Any(p => p.Login == newLogin && !p.IsDeleted))
                        throw new InvalidOperationException($"Пользователь с логином '{newLogin}' уже существует.");
                    updated.Login = newLogin;
                    changed.Add("login");
                }
                if (!string.IsNullOrEmpty(displayName)) { updated.DisplayName = displayName; changed.Add("displayName"); }
                if (!string.IsNullOrEmpty(isAdmin))
                {
                    updated.IsAdmin = ParseBool(isAdmin, "isAdmin");
                    changed.Add("isAdmin");
                }
                if (!string.IsNullOrEmpty(isInactive))
                {
                    var inactive = ParseBool(isInactive, "isInactive");
                    updated.IsInactive = inactive;
                    updated.AccountState = inactive ? AccountStates.Inactive : AccountStates.Active;
                    changed.Add("isInactive");
                }
                var passwordEncrypted = string.IsNullOrEmpty(password) ? null : password.EncryptAes();
                if (passwordEncrypted != null) changed.Add("password");

                if (changed.Count == 0)
                    throw new InvalidOperationException("Не указаны поля для обновления.");

                var change = passwordEncrypted != null
                    ? new DPersonChangeData(old, updated, passwordEncrypted)
                    : new DPersonChangeData(old, updated);
                await admin.ChangePeopleAsync(_connection.DatabaseName, new DPersonChangeset(change));
                return null;
            });
            return ToolResult.Ok($"Пользователь '{login}' обновлён ({string.Join(", ", changed)}).");
        });
    }

    [McpServerTool, Description(
        "Удалить пользователя (пометить IsDeleted, как это делает myAdmin). Требует учётку администратора сервера.")]
    public Task<string> DeletePerson(
        [Description("Логин пользователя")] string login)
    {
        return ToolRunner.RunWriteAsync(_connection, "DeletePerson", $"login={login}", async () =>
        {
            _connection.EnsureConnected();
            await _connection.WithAdminApiAsync<object?>(async admin =>
            {
                var old = await FindPersonAsync(admin, login);
                if (old.Id == _connection.CurrentPersonId)
                    throw new InvalidOperationException("Нельзя удалить пользователя, под которым выполнено подключение.");
                var updated = old.Clone();
                updated.IsDeleted = true;
                await admin.ChangePeopleAsync(_connection.DatabaseName, new DPersonChangeset(new DPersonChangeData(old, updated)));
                return null;
            });
            return ToolResult.Ok($"Пользователь '{login}' помечен удалённым.");
        });
    }

    // ===== Оргструктура =====

    [McpServerTool, Description(
        "Создать организационную единицу. kind: Department (отдел), Position (должность), Group (группа). " +
        "Требует учётку администратора сервера.")]
    public Task<string> CreateOrgUnit(
        [Description("Название")] string title,
        [Description("Вид: Department, Position, Group")] string kind = "Position",
        [Description("ID родительской оргединицы (0 = корень)")] int parentId = 0,
        [Description("Руководящая должность (true/false)")] bool isBoss = false)
    {
        return ToolRunner.RunWriteAsync(_connection, "CreateOrgUnit", $"title={title}, kind={kind}", async () =>
        {
            _connection.EnsureConnected();
            var createdId = await _connection.WithAdminApiAsync(async admin =>
            {
                if (!Enum.TryParse<OrgUnitKind>(kind, true, out var unitKind))
                    throw new InvalidOperationException($"Неизвестный вид оргединицы '{kind}'. Допустимо: {string.Join(", ", Enum.GetNames<OrgUnitKind>())}.");

                var units = await admin.LoadOrganisationUnitsAsync(_connection.DatabaseName);
                var parent = units.FirstOrDefault(u => u.Id == parentId)
                    ?? throw new InvalidOperationException($"Родительская оргединица #{parentId} не найдена.");

                var created = new DOrganisationUnit
                {
                    Id = -1,
                    Kind = unitKind,
                    Title = title,
                    IsBoss = isBoss,
                };
                var result = await admin.ChangeOrganisationUnitAsync(_connection.DatabaseName, new DOrganisationUnitChangesetData
                {
                    Created = created,
                    Updated = parent.Clone(),
                });
                if (!result.CreatedId.HasValue)
                    throw new InvalidOperationException("Сервер не вернул ID новой оргединицы.");
                return result.CreatedId.Value;
            });
            return ToolResult.Ok($"Оргединица '{title}' создана (ID: {createdId}, родитель #{parentId}).");
        });
    }

    [McpServerTool, Description(
        "Изменить организационную единицу. Пустые параметры не меняются. Требует учётку администратора сервера.")]
    public Task<string> UpdateOrgUnit(
        [Description("ID оргединицы")] int unitId,
        [Description("Новое название (пусто = не менять)")] string title = "",
        [Description("Руководящая: true/false (пусто = не менять)")] string isBoss = "")
    {
        return ToolRunner.RunWriteAsync(_connection, "UpdateOrgUnit", $"id={unitId}", async () =>
        {
            _connection.EnsureConnected();
            var changed = new List<string>();
            await _connection.WithAdminApiAsync<object?>(async admin =>
            {
                var unit = await FindOrgUnitAsync(admin, unitId);
                var updated = unit.Clone();

                if (!string.IsNullOrEmpty(title)) { updated.Title = title; changed.Add("title"); }
                if (!string.IsNullOrEmpty(isBoss))
                {
                    updated.IsBoss = ParseBool(isBoss, "isBoss");
                    changed.Add("isBoss");
                }

                if (changed.Count == 0)
                    throw new InvalidOperationException("Не указаны поля для обновления.");

                await admin.ChangeOrganisationUnitAsync(_connection.DatabaseName, new DOrganisationUnitChangesetData
                {
                    Updated = updated,
                });
                return null;
            });
            return ToolResult.Ok($"Оргединица #{unitId} обновлена ({string.Join(", ", changed)}).");
        });
    }

    [McpServerTool, Description(
        "Удалить организационную единицу (пометить IsDeleted). Отклоняется, если есть неудалённые дочерние. " +
        "Требует учётку администратора сервера.")]
    public Task<string> DeleteOrgUnit(
        [Description("ID оргединицы")] int unitId)
    {
        return ToolRunner.RunWriteAsync(_connection, "DeleteOrgUnit", $"id={unitId}", async () =>
        {
            _connection.EnsureConnected();
            await _connection.WithAdminApiAsync<object?>(async admin =>
            {
                var units = await admin.LoadOrganisationUnitsAsync(_connection.DatabaseName);
                var unit = units.FirstOrDefault(u => u.Id == unitId)
                    ?? throw new InvalidOperationException($"Оргединица #{unitId} не найдена.");

                var aliveChildren = unit.Children != null
                    ? units.Where(u => unit.Children.Contains(u.Id) && !u.IsDeleted).Select(u => $"#{u.Id} {u.Title}").ToList()
                    : new List<string>();
                if (aliveChildren.Count > 0)
                    throw new InvalidOperationException(
                        $"У оргединицы #{unitId} есть неудалённые дочерние: {string.Join(", ", aliveChildren)}. Сначала удалите или перенесите их.");

                var updated = unit.Clone();
                updated.IsDeleted = true;
                await admin.ChangeOrganisationUnitAsync(_connection.DatabaseName, new DOrganisationUnitChangesetData
                {
                    Updated = updated,
                });
                return null;
            });
            return ToolResult.Ok($"Оргединица #{unitId} помечена удалённой.");
        });
    }

    [McpServerTool, Description(
        "Переместить организационные единицы к новому родителю. Требует учётку администратора сервера.")]
    public Task<string> MoveOrgUnits(
        [Description("ID оргединиц через запятую")] string unitIds,
        [Description("ID новой родительской оргединицы (0 = корень)")] int newParentId)
    {
        return ToolRunner.RunWriteAsync(_connection, "MoveOrgUnits", $"ids={unitIds}, parent={newParentId}", async () =>
        {
            _connection.EnsureConnected();
            var ids = unitIds.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(s => int.TryParse(s, out var id) ? id : -1)
                .Where(id => id > 0)
                .ToArray();
            if (ids.Length == 0)
                return ToolResult.Error("Не указаны корректные ID оргединиц.");

            await _connection.WithAdminApiAsync<object?>(async admin =>
            {
                await admin.MoveOrganisationUnitsAsync(_connection.DatabaseName, ids, newParentId);
                return null;
            });
            return ToolResult.Ok($"Оргединицы {string.Join(", ", ids.Select(i => $"#{i}"))} перемещены к родителю #{newParentId}.");
        });
    }

    [McpServerTool, Description(
        "Назначить пользователя на должность (позицию). Требует учётку администратора сервера.")]
    public Task<string> SetPersonOnPosition(
        [Description("ID оргединицы-должности")] int positionId,
        [Description("ID пользователя (personId)")] int personId)
    {
        return ToolRunner.RunWriteAsync(_connection, "SetPersonOnPosition", $"position={positionId}, person={personId}", async () =>
        {
            _connection.EnsureConnected();
            await _connection.WithAdminApiAsync<object?>(async admin =>
            {
                var unit = await FindOrgUnitAsync(admin, positionId);
                if (unit.Kind != OrgUnitKind.Position)
                    throw new InvalidOperationException($"Оргединица #{positionId} — {unit.Kind}, назначать людей можно только на должности (Position).");
                await admin.SetPeopleOnPositionAsync(_connection.DatabaseName, new DPersonOnPositionData
                {
                    PositionId = positionId,
                    Person = personId,
                });
                return null;
            });
            return ToolResult.Ok($"Пользователь #{personId} назначен на должность #{positionId}.");
        });
    }

    [McpServerTool, Description(
        "Снять пользователя с должности (позиции). Требует учётку администратора сервера.")]
    public Task<string> RemovePersonFromPosition(
        [Description("ID оргединицы-должности")] int positionId)
    {
        return ToolRunner.RunWriteAsync(_connection, "RemovePersonFromPosition", $"position={positionId}", async () =>
        {
            _connection.EnsureConnected();
            await _connection.WithAdminApiAsync<object?>(async admin =>
            {
                var unit = await FindOrgUnitAsync(admin, positionId);
                await admin.SetPeopleOnPositionAsync(_connection.DatabaseName, new DPersonOnPositionData
                {
                    PositionId = positionId,
                    Person = unit.Person > 0 ? unit.Person : null,
                    IsRemoving = true,
                });
                return null;
            });
            return ToolResult.Ok($"Должность #{positionId} освобождена.");
        });
    }

    // ===== Вспомогательные =====

    private static bool ParseBool(string value, string paramName)
    {
        if (value.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
        if (value.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
        throw new InvalidOperationException($"Некорректное значение {paramName}: '{value}' (ожидается true/false).");
    }

    private async Task<DPerson> FindPersonAsync(IServerAdminAsyncApi admin, string login)
    {
        var people = await admin.LoadPeopleAsync(_connection.DatabaseName);
        return people.FirstOrDefault(p => p.Login == login && !p.IsDeleted)
            ?? throw new InvalidOperationException($"Пользователь '{login}' не найден.");
    }

    private async Task<DOrganisationUnit> FindOrgUnitAsync(IServerAdminAsyncApi admin, int unitId)
    {
        var units = await admin.LoadOrganisationUnitsAsync(_connection.DatabaseName);
        return units.FirstOrDefault(u => u.Id == unitId)
            ?? throw new InvalidOperationException($"Оргединица #{unitId} не найдена.");
    }
}
