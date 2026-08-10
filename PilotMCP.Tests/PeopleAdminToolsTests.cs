using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PilotMCP.Tests;

/// <summary>
/// Интеграционные тесты управления пользователями и оргструктурой.
/// Требуют учётку администратора сервера (PILOT_ADMIN_LOGIN/PILOT_ADMIN_PASSWORD).
/// Если админ-авторизация недоступна — тесты логируют причину и мягко пропускаются.
/// </summary>
[Collection("Pilot")]
public class PeopleAdminToolsTests
{
    private readonly McpTestClient _client;
    public PeopleAdminToolsTests(PilotFixture fixture) => _client = fixture.Client;

    private static int ParseId(string message, string what)
    {
        var m = Regex.Match(message, @"ID: (\d+)");
        Assert.True(m.Success, $"Не удалось извлечь ID {what} из: {message}");
        return int.Parse(m.Groups[1].Value);
    }

    [Fact]
    public async Task Person_Lifecycle_CreateUpdateDelete()
    {
        var login = $"mcp_test_user_{Guid.NewGuid():N}";

        var create = await _client.CallToolAsync("CreatePerson", new JsonObject
        {
            ["login"] = login,
            ["displayName"] = "MCP тестовый пользователь",
            ["password"] = "Test!Password123",
            ["email"] = "mcp-test@example.com",
        }, 60000);

        if (AdminGate.ShouldSkip(create, "PersonLifecycle"))
            return;

        try
        {
            var personId = ParseId(create.Message, "пользователя");

            // Пользователь виден через обычный инструмент чтения
            var read = await _client.CallToolAsync("GetPeopleByIds", new JsonObject
            {
                ["personIds"] = personId.ToString(),
            }, 60000);
            read.AssertOk();
            Assert.Contains(login, read.RawText);

            var update = await _client.CallToolAsync("UpdatePersonAdmin", new JsonObject
            {
                ["login"] = login,
                ["displayName"] = "MCP тестовый пользователь (обновлён)",
                ["password"] = "New!Password456",
                ["isInactive"] = "true",
            }, 60000);
            update.AssertOk();
            Assert.Contains("password", update.Message);

            // Повторное создание с тем же логином — ошибка
            var duplicate = await _client.CallToolAsync("CreatePerson", new JsonObject
            {
                ["login"] = login,
                ["displayName"] = "дубль",
                ["password"] = "x",
            }, 60000);
            AdminGate.AssertErrorBecause(duplicate, "уже существует");
        }
        finally
        {
            var delete = await _client.CallToolAsync("DeletePerson", new JsonObject
            {
                ["login"] = login,
            }, 60000);
            delete.AssertOk();
        }
    }

    [Fact]
    public async Task Person_PasswordIsActuallyApplied_CanLogin()
    {
        var login = $"mcp_test_pwd_{Guid.NewGuid():N}";
        const string firstPassword = "First!Password123";
        const string secondPassword = "Second!Password456";

        var create = await _client.CallToolAsync("CreatePerson", new JsonObject
        {
            ["login"] = login,
            ["displayName"] = "MCP проверка пароля",
            ["password"] = firstPassword,
        }, 60000);

        if (AdminGate.ShouldSkip(create, "Password"))
            return;

        var personId = ParseId(create.Message, "пользователя");
        int deptId = 0, posId = 0;
        try
        {
            // Войти можно только с должности — создаём отдел, должность и назначаем пользователя
            var createDept = await _client.CallToolAsync("CreateOrgUnit", new JsonObject
            {
                ["title"] = $"mcp_test_pwddept_{Guid.NewGuid():N}",
                ["kind"] = "Department",
            }, 60000);
            createDept.AssertOk();
            deptId = ParseId(createDept.Message, "отдела");

            var createPos = await _client.CallToolAsync("CreateOrgUnit", new JsonObject
            {
                ["title"] = $"mcp_test_pwdpos_{Guid.NewGuid():N}",
                ["kind"] = "Position",
                ["parentId"] = deptId,
            }, 60000);
            createPos.AssertOk();
            posId = ParseId(createPos.Message, "должности");

            var assign = await _client.CallToolAsync("SetPersonOnPosition", new JsonObject
            {
                ["positionId"] = posId,
                ["personId"] = personId,
            }, 60000);
            assign.AssertOk();

            // Пароль, заданный при создании, реально работает
            PilotLoginProbe.AssertCanLogin(login, firstPassword);
            Assert.False(PilotLoginProbe.CanLogin(login, secondPassword), "Пользователь не должен входить по ещё не заданному паролю");

            var update = await _client.CallToolAsync("UpdatePersonAdmin", new JsonObject
            {
                ["login"] = login,
                ["password"] = secondPassword,
            }, 60000);
            update.AssertOk();

            // После смены работает новый пароль, старый — нет
            PilotLoginProbe.AssertCanLogin(login, secondPassword);
            Assert.False(PilotLoginProbe.CanLogin(login, firstPassword), "Старый пароль должен перестать работать");
        }
        finally
        {
            await _client.CallToolAsync("DeletePerson", new JsonObject { ["login"] = login }, 60000);
            if (posId > 0)
                await _client.CallToolAsync("DeleteOrgUnit", new JsonObject { ["unitId"] = posId }, 60000);
            if (deptId > 0)
                await _client.CallToolAsync("DeleteOrgUnit", new JsonObject { ["unitId"] = deptId }, 60000);
        }
    }

    [Fact]
    public async Task DeletePerson_MakesPersonUnavailable()
    {
        var login = $"mcp_test_del_{Guid.NewGuid():N}";
        var create = await _client.CallToolAsync("CreatePerson", new JsonObject
        {
            ["login"] = login,
            ["displayName"] = "MCP удаляемый",
            ["password"] = "Test!Password123",
        }, 60000);

        if (AdminGate.ShouldSkip(create, "DeletePerson"))
            return;

        var delete = await _client.CallToolAsync("DeletePerson", new JsonObject { ["login"] = login }, 60000);
        delete.AssertOk();

        // Удалённый пользователь больше не находится инструментами изменения
        var afterDelete = await _client.CallToolAsync("UpdatePersonAdmin", new JsonObject
        {
            ["login"] = login,
            ["displayName"] = "x",
        }, 60000);
        AdminGate.AssertErrorBecause(afterDelete, "не найден");
    }

    [Fact]
    public async Task UpdatePersonAdmin_NotFound_ReturnsError()
    {
        var result = await _client.CallToolAsync("UpdatePersonAdmin", new JsonObject
        {
            ["login"] = $"mcp_nonexistent_{Guid.NewGuid():N}",
            ["displayName"] = "x",
        }, 60000);

        AdminGate.AssertErrorBecause(result, "не найден");
    }

    [Fact]
    public async Task UpdatePersonAdmin_NoFields_ReturnsError()
    {
        var login = $"mcp_test_nofields_{Guid.NewGuid():N}";
        var create = await _client.CallToolAsync("CreatePerson", new JsonObject
        {
            ["login"] = login,
            ["displayName"] = "MCP без полей",
            ["password"] = "Test!Password123",
        }, 60000);

        if (AdminGate.ShouldSkip(create, "NoFields"))
            return;

        try
        {
            var update = await _client.CallToolAsync("UpdatePersonAdmin", new JsonObject
            {
                ["login"] = login,
            }, 60000);
            AdminGate.AssertErrorBecause(update, "Не указаны поля");
        }
        finally
        {
            await _client.CallToolAsync("DeletePerson", new JsonObject { ["login"] = login }, 60000);
        }
    }

    [Fact]
    public async Task OrgStructure_Lifecycle_DepartmentPositionAndAssignment()
    {
        var deptTitle = $"mcp_test_dept_{Guid.NewGuid():N}";
        var posTitle = $"mcp_test_pos_{Guid.NewGuid():N}";
        var login = $"mcp_test_orguser_{Guid.NewGuid():N}";

        var createDept = await _client.CallToolAsync("CreateOrgUnit", new JsonObject
        {
            ["title"] = deptTitle,
            ["kind"] = "Department",
            ["parentId"] = 0,
        }, 60000);

        if (AdminGate.ShouldSkip(createDept, "OrgStructure"))
            return;

        int deptId = ParseId(createDept.Message, "отдела");
        int posId = 0;
        int dept2Id = 0;
        var personCreated = false;
        var deptDeleted = false;
        try
        {
            // Оргединица видна через обычный инструмент чтения
            var readDept = await _client.CallToolAsync("GetOrgUnitsByIds", new JsonObject
            {
                ["unitIds"] = deptId.ToString(),
            }, 60000);
            readDept.AssertOk();
            Assert.Contains(deptTitle, readDept.RawText);

            var updateDept = await _client.CallToolAsync("UpdateOrgUnit", new JsonObject
            {
                ["unitId"] = deptId,
                ["title"] = deptTitle + "_renamed",
            }, 60000);
            updateDept.AssertOk();

            // Должность внутри отдела
            var createPos = await _client.CallToolAsync("CreateOrgUnit", new JsonObject
            {
                ["title"] = posTitle,
                ["kind"] = "Position",
                ["parentId"] = deptId,
            }, 60000);
            createPos.AssertOk();
            posId = ParseId(createPos.Message, "должности");

            // Отдел с неудалённым потомком удалить нельзя
            var deleteBusy = await _client.CallToolAsync("DeleteOrgUnit", new JsonObject
            {
                ["unitId"] = deptId,
            }, 60000);
            AdminGate.AssertErrorBecause(deleteBusy, "неудалённые дочерние");

            // Назначение пользователя на должность
            var createPerson = await _client.CallToolAsync("CreatePerson", new JsonObject
            {
                ["login"] = login,
                ["displayName"] = "MCP пользователь для позиции",
                ["password"] = "Test!Password123",
            }, 60000);
            createPerson.AssertOk();
            personCreated = true;
            var personId = ParseId(createPerson.Message, "пользователя");

            var assign = await _client.CallToolAsync("SetPersonOnPosition", new JsonObject
            {
                ["positionId"] = posId,
                ["personId"] = personId,
            }, 60000);
            assign.AssertOk();

            var readPos = await _client.CallToolAsync("GetOrgUnitsByIds", new JsonObject
            {
                ["unitIds"] = posId.ToString(),
            }, 60000);
            readPos.AssertOk();
            Assert.Contains($"#{personId}", readPos.RawText);

            var unassign = await _client.CallToolAsync("RemovePersonFromPosition", new JsonObject
            {
                ["positionId"] = posId,
            }, 60000);
            unassign.AssertOk();

            // Перенос должности в другой отдел
            var createDept2 = await _client.CallToolAsync("CreateOrgUnit", new JsonObject
            {
                ["title"] = deptTitle + "_second",
                ["kind"] = "Department",
                ["parentId"] = 0,
            }, 60000);
            createDept2.AssertOk();
            dept2Id = ParseId(createDept2.Message, "второго отдела");

            var move = await _client.CallToolAsync("MoveOrgUnits", new JsonObject
            {
                ["unitIds"] = posId.ToString(),
                ["newParentId"] = dept2Id,
            }, 60000);
            move.AssertOk();

            // После переноса первый отдел пуст и удаляется, второй — нет
            var deleteEmptied = await _client.CallToolAsync("DeleteOrgUnit", new JsonObject { ["unitId"] = deptId }, 60000);
            deleteEmptied.AssertOk();
            deptDeleted = true;

            var deleteBusy2 = await _client.CallToolAsync("DeleteOrgUnit", new JsonObject { ["unitId"] = dept2Id }, 60000);
            AdminGate.AssertErrorBecause(deleteBusy2, "неудалённые дочерние");

            // Должность нельзя перенести в корень — ограничение сервера
            var moveToRoot = await _client.CallToolAsync("MoveOrgUnits", new JsonObject
            {
                ["unitIds"] = posId.ToString(),
                ["newParentId"] = 0,
            }, 60000);
            Assert.Equal("error", moveToRoot.Status);
        }
        finally
        {
            if (personCreated)
                await _client.CallToolAsync("DeletePerson", new JsonObject { ["login"] = login }, 60000);
            if (posId > 0)
                await _client.CallToolAsync("DeleteOrgUnit", new JsonObject { ["unitId"] = posId }, 60000);
            if (dept2Id > 0)
                await _client.CallToolAsync("DeleteOrgUnit", new JsonObject { ["unitId"] = dept2Id }, 60000);
            if (!deptDeleted)
                await _client.CallToolAsync("DeleteOrgUnit", new JsonObject { ["unitId"] = deptId }, 60000);
        }
    }

    [Fact]
    public async Task CreateOrgUnit_UnknownKind_ReturnsError()
    {
        var result = await _client.CallToolAsync("CreateOrgUnit", new JsonObject
        {
            ["title"] = "mcp_never_created",
            ["kind"] = "NoSuchKind",
        }, 60000);

        AdminGate.AssertErrorBecause(result, "NoSuchKind");
    }

    [Fact]
    public async Task SetPersonOnPosition_NotAPosition_ReturnsError()
    {
        var deptTitle = $"mcp_test_notpos_{Guid.NewGuid():N}";
        var create = await _client.CallToolAsync("CreateOrgUnit", new JsonObject
        {
            ["title"] = deptTitle,
            ["kind"] = "Department",
        }, 60000);

        if (AdminGate.ShouldSkip(create, "NotAPosition"))
            return;

        var deptId = ParseId(create.Message, "отдела");
        try
        {
            // На отдел назначать людей нельзя — только на должность
            var assign = await _client.CallToolAsync("SetPersonOnPosition", new JsonObject
            {
                ["positionId"] = deptId,
                ["personId"] = 1,
            }, 60000);
            AdminGate.AssertErrorBecause(assign, "Position");
        }
        finally
        {
            await _client.CallToolAsync("DeleteOrgUnit", new JsonObject { ["unitId"] = deptId }, 60000);
        }
    }

    [Fact]
    public async Task DeleteOrgUnit_NotFound_ReturnsError()
    {
        var result = await _client.CallToolAsync("DeleteOrgUnit", new JsonObject
        {
            ["unitId"] = 999999999,
        }, 60000);

        AdminGate.AssertErrorBecause(result, "не найдена");
    }

    [Fact]
    public async Task MoveOrgUnits_InvalidIdInList_MovesNothing()
    {
        // Некорректный элемент отклоняет весь список: частичный перенос с рапортом об успехе недопустим.
        // ID заведомо несуществующие — если валидация регрессирует, тест не тронет реальную оргструктуру.
        var result = await _client.CallToolAsync("MoveOrgUnits", new JsonObject
        {
            ["unitIds"] = "999999998,abc,999999999",
            ["newParentId"] = 999999997,
        }, 60000);

        Assert.Equal("error", result.Status);
        Assert.Contains("abc", result.Message);
    }

    [Fact]
    public async Task RemovePersonFromPosition_EmptyPosition_ReturnsError()
    {
        var posTitle = $"mcp_test_freepos_{Guid.NewGuid():N}";
        var createDept = await _client.CallToolAsync("CreateOrgUnit", new JsonObject
        {
            ["title"] = $"mcp_test_freedept_{Guid.NewGuid():N}",
            ["kind"] = "Department",
        }, 60000);

        if (AdminGate.ShouldSkip(createDept, "EmptyPosition"))
            return;

        var deptId = ParseId(createDept.Message, "отдела");
        var posId = 0;
        try
        {
            var createPos = await _client.CallToolAsync("CreateOrgUnit", new JsonObject
            {
                ["title"] = posTitle,
                ["kind"] = "Position",
                ["parentId"] = deptId,
            }, 60000);
            createPos.AssertOk();
            posId = ParseId(createPos.Message, "должности");

            // На только что созданной должности никого нет — освобождать нечего
            var remove = await _client.CallToolAsync("RemovePersonFromPosition", new JsonObject
            {
                ["positionId"] = posId,
            }, 60000);
            AdminGate.AssertErrorBecause(remove, "никто не назначен");
        }
        finally
        {
            if (posId > 0)
                await _client.CallToolAsync("DeleteOrgUnit", new JsonObject { ["unitId"] = posId }, 60000);
            await _client.CallToolAsync("DeleteOrgUnit", new JsonObject { ["unitId"] = deptId }, 60000);
        }
    }
}
