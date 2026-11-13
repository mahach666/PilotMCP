using Ascon.Pilot.DataClasses;
using Ascon.Pilot.Common;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace PilotMCP;

[McpServerToolType]
public class AdminTools
{
    private readonly PilotConnection _connection;
    public AdminTools(PilotConnection connection) => _connection = connection;

    [McpServerTool, Description(
        "Обновить данные пользователя по personId через админский API: email, телефон, комментарий, " +
        "признак неактивности. Требует учётку администратора сервера. " +
        "Все параметры, кроме personId, необязательны — обновляются только указанные.")]
    public Task<string> UpdatePerson(
        [Description("ID пользователя (personId)")] int personId,
        [Description("Новый email (пусто = не менять)")] string email = "",
        [Description("Новый телефон (пусто = не менять)")] string phone = "",
        [Description("Новый комментарий (пусто = не менять)")] string comment = "",
        [Description("Сделать неактивным: true/false (пусто = не менять)")] string isInactive = "")
    {
        return ToolRunner.RunWriteAsync(_connection, "UpdatePerson",
            $"person={personId}, email={email}, phone={phone}, comment={comment}, isInactive={isInactive}", async () =>
        {
            if (personId <= 0)
                return ToolResult.Error("personId должен быть положительным числом.");
            if (string.IsNullOrEmpty(email) && string.IsNullOrEmpty(phone)
                && string.IsNullOrEmpty(comment) && string.IsNullOrEmpty(isInactive))
                return ToolResult.Error("Не указаны поля для обновления.");

            // Клиентский UpdatePersonAsync правит только подключённого пользователя и запрещает email/phone,
            // поэтому изменение идёт через админский API, как в UpdatePersonAdmin.
            bool? inactive = string.IsNullOrEmpty(isInactive)
                ? null : PeopleAdminTools.ParseBool(isInactive, "isInactive");

            _connection.EnsureConnected();
            await _connection.WithAdminApiAsync<object?>(async admin =>
            {
                var people = await admin.LoadPeopleAsync(_connection.DatabaseName);
                var old = people.FirstOrDefault(p => p.Id == personId && !p.IsDeleted)
                    ?? throw new InvalidOperationException($"Пользователь #{personId} не найден.");
                var updated = old.Clone();
                if (!string.IsNullOrEmpty(email)) updated.Email = email;
                if (!string.IsNullOrEmpty(phone)) updated.Phone = phone;
                if (!string.IsNullOrEmpty(comment)) updated.Comment = comment;
                if (inactive.HasValue)
                {
                    updated.IsInactive = inactive.Value;
                    updated.AccountState = inactive.Value ? AccountStates.Inactive : AccountStates.Active;
                }
                await admin.ChangePeopleAsync(_connection.DatabaseName, new DPersonChangeset(new DPersonChangeData(old, updated)));
                return null;
            });
            return ToolResult.Ok($"Данные пользователя #{personId} обновлены.");
        });
    }

    [McpServerTool, Description(
        "Изменить настройку пользователя или общую настройку базы. " +
        "Можно менять настройки ЛЮБОГО пользователя по его personId. " +
        "personId>0 — персональная настройка этого пользователя, orgUnitId>0 — настройка оргединицы. " +
        "Нужно указать хотя бы одно из двух (общей настройки через этот API нет).")]
    public Task<string> ChangeSettings(
        [Description("Ключ настройки (раздел)")] string key,
        [Description("ID пользователя (personId). 0 = не задан")] int personId = 0,
        [Description("ID оргединицы (0 = не задан)")] int orgUnitId = 0,
        [Description("Значение настройки (строка)")] string value = "")
    {
        return ToolRunner.RunWriteAsync(_connection, "ChangeSettings",
            $"key={key}, personId={personId}, orgUnitId={orgUnitId}", async () =>
        {
            if (string.IsNullOrWhiteSpace(key))
                return ToolResult.Error("Не указан ключ настройки.");
            if (personId < 0 || orgUnitId < 0)
                return ToolResult.Error("personId и orgUnitId не могут быть отрицательными.");
            if (personId == 0 && orgUnitId == 0)
                return ToolResult.Error("Укажите personId или orgUnitId: сервер не принимает настройку без адресата.");

            // На сервере "не задан" = -1; 0 он воспринял бы как оргединицу #0.
            var change = new DSettingsChange
            {
                Identity = Guid.NewGuid(),
                Key = key,
                Value = value,
                PersonId = personId > 0 ? personId : -1,
                OrgUnitId = orgUnitId > 0 ? orgUnitId : -1
            };
            await _connection.ServerApi.ChangeSettingsAsync(change);
            return ToolResult.Ok($"Настройка \"{key}\" обновлена.");
        });
    }
}
