using System.Text.Json.Nodes;

namespace PilotMCP.Tests;

/// <summary>
/// Интеграционные тесты инструментов редактирования метаданных.
/// Требуют учётку администратора сервера (PILOT_ADMIN_LOGIN/PILOT_ADMIN_PASSWORD
/// или основной логин с правами администратора сервера).
/// Если админ-авторизация недоступна — тесты логируют причину и мягко пропускаются.
/// </summary>
[Collection("Pilot")]
public class MetadataAdminToolsTests
{
    private readonly McpTestClient _client;
    public MetadataAdminToolsTests(PilotFixture fixture) => _client = fixture.Client;

    [Fact]
    public async Task AutomationConfig_Lifecycle_CreateUpdateDelete()
    {
        var name = $"mcp_test_{Guid.NewGuid():N}";

        var create = await _client.CallToolAsync("CreateAutomationConfig", new JsonObject
        {
            ["name"] = name,
            ["triggers"] = "[]",
            ["cSharpCode"] = "// mcp integration test",
            ["isPaused"] = true,
        }, 60000);

        if (AdminGate.ShouldSkip(create, "AutomationLifecycle"))
            return;

        try
        {
            Assert.Contains(name, create.Message);

            var update = await _client.CallToolAsync("UpdateAutomationConfig", new JsonObject
            {
                ["name"] = name,
                ["cSharpCode"] = "// mcp integration test (updated)",
            }, 60000);
            update.AssertOk();
            Assert.Contains("cSharpCode", update.Message);

            // Автоматизация видна через обычный инструмент чтения
            var list = await _client.CallToolAsync("GetAutomationConfigs", null, 60000);
            list.AssertOk();
            Assert.NotNull(list.ResultFile);
            var content = await File.ReadAllTextAsync(list.ResultFile!);
            Assert.Contains(name, content);
        }
        finally
        {
            var delete = await _client.CallToolAsync("DeleteAutomationConfig", new JsonObject
            {
                ["name"] = name,
            }, 60000);
            delete.AssertOk();
        }
    }

    [Fact]
    public async Task Type_And_Attributes_Lifecycle()
    {
        var typeName = $"mcp_test_type_{Guid.NewGuid():N}";
        var attrName = "mcp_test_attr";

        var create = await _client.CallToolAsync("CreateType", new JsonObject
        {
            ["name"] = typeName,
            ["title"] = "MCP тестовый тип",
        }, 60000);

        if (AdminGate.ShouldSkip(create, "TypeLifecycle"))
            return;

        try
        {
            Assert.Contains(typeName, create.Message);

            var update = await _client.CallToolAsync("UpdateType", new JsonObject
            {
                ["name"] = typeName,
                ["title"] = "MCP тестовый тип (обновлён)",
                ["hasFiles"] = "true",
            }, 60000);
            update.AssertOk();

            var addAttr = await _client.CallToolAsync("AddTypeAttribute", new JsonObject
            {
                ["typeName"] = typeName,
                ["name"] = attrName,
                ["title"] = "Тестовый атрибут",
                ["attrType"] = "String",
                ["obligatory"] = true,
            }, 60000);
            addAttr.AssertOk();

            var updateAttr = await _client.CallToolAsync("UpdateTypeAttribute", new JsonObject
            {
                ["typeName"] = typeName,
                ["name"] = attrName,
                ["title"] = "Тестовый атрибут (обновлён)",
                ["obligatory"] = "false",
            }, 60000);
            updateAttr.AssertOk();

            // Тип и атрибут видны через обычный инструмент чтения (кеш метаданных обновился)
            var getType = await _client.CallToolAsync("GetType", new JsonObject
            {
                ["typeName"] = typeName,
            }, 60000);
            Assert.Contains(typeName, getType.RawText);
            Assert.Contains(attrName, getType.RawText);

            var deleteAttr = await _client.CallToolAsync("DeleteTypeAttribute", new JsonObject
            {
                ["typeName"] = typeName,
                ["name"] = attrName,
            }, 60000);
            deleteAttr.AssertOk();
        }
        finally
        {
            var delete = await _client.CallToolAsync("DeleteType", new JsonObject
            {
                ["name"] = typeName,
            }, 60000);
            delete.AssertOk();
        }
    }

    [Fact]
    public async Task AddTypeChildren_LinksChildToParent()
    {
        var parentName = $"mcp_test_parent_{Guid.NewGuid():N}";
        var childName = $"mcp_test_child_{Guid.NewGuid():N}";

        var createParent = await _client.CallToolAsync("CreateType", new JsonObject
        {
            ["name"] = parentName,
            ["title"] = "MCP тестовый родитель",
        }, 60000);

        if (AdminGate.ShouldSkip(createParent, "TypeChildren"))
            return;

        var child2Name = $"mcp_test_child2_{Guid.NewGuid():N}";
        var childDeleted = false;
        try
        {
            var createChild = await _client.CallToolAsync("CreateType", new JsonObject
            {
                ["name"] = childName,
                ["title"] = "MCP тестовый ребёнок",
            }, 60000);
            createChild.AssertOk();

            var addChildren = await _client.CallToolAsync("AddTypeChildren", new JsonObject
            {
                ["parentTypeName"] = parentName,
                ["childTypeNames"] = childName,
            }, 60000);
            addChildren.AssertOk();
            Assert.Contains(childName, addChildren.Message);

            // Повторное добавление того же ребёнка — ошибка «уже дочерний»
            var addAgain = await _client.CallToolAsync("AddTypeChildren", new JsonObject
            {
                ["parentTypeName"] = parentName,
                ["childTypeNames"] = childName,
            }, 60000);
            AdminGate.AssertErrorBecause(addAgain, "уже являются дочерними");

            // Создание типа сразу с родителем (parentTypeNames)
            var createChild2 = await _client.CallToolAsync("CreateType", new JsonObject
            {
                ["name"] = child2Name,
                ["title"] = "MCP тестовый ребёнок 2",
                ["parentTypeNames"] = parentName,
            }, 60000);
            createChild2.AssertOk();

            // Удаление типа снимает обратную ссылку из Children родителя
            var deleteChild = await _client.CallToolAsync("DeleteType", new JsonObject
            {
                ["name"] = childName,
            }, 60000);
            deleteChild.AssertOk();
            childDeleted = true;
            Assert.Contains("Убран из дочерних у:", deleteChild.Message);
            Assert.Contains(parentName, deleteChild.Message);
        }
        finally
        {
            await _client.CallToolAsync("DeleteType", new JsonObject { ["name"] = child2Name }, 60000);
            if (!childDeleted)
                await _client.CallToolAsync("DeleteType", new JsonObject { ["name"] = childName }, 60000);
            await _client.CallToolAsync("DeleteType", new JsonObject { ["name"] = parentName }, 60000);
        }
    }

    [Fact]
    public async Task AddTypeAttribute_UnknownAttrType_ReturnsError()
    {
        // Ошибка парсинга типа атрибута возникает до записи метаданных —
        // но требует существующего типа, берём системный Root_object_type
        var result = await _client.CallToolAsync("AddTypeAttribute", new JsonObject
        {
            ["typeName"] = "Root_object_type",
            ["name"] = "mcp_never_created",
            ["title"] = "x",
            ["attrType"] = "NoSuchType",
        }, 60000);

        AdminGate.AssertErrorBecause(result, "NoSuchType");
    }

    [Fact]
    public async Task UserState_Lifecycle_CreateUpdateDelete()
    {
        var stateName = $"mcp_test_state_{Guid.NewGuid():N}";

        var create = await _client.CallToolAsync("CreateUserState", new JsonObject
        {
            ["name"] = stateName,
            ["title"] = "MCP тестовое состояние",
            ["color"] = "Color1",
        }, 60000);

        if (AdminGate.ShouldSkip(create, "UserState"))
            return;

        try
        {
            Assert.Contains(stateName, create.Message);

            var update = await _client.CallToolAsync("UpdateUserState", new JsonObject
            {
                ["name"] = stateName,
                ["title"] = "MCP тестовое состояние (обновлено)",
                ["color"] = "Color2",
                ["isCompletionState"] = "true",
            }, 60000);
            update.AssertOk();

            var list = await _client.CallToolAsync("GetUserStates", null, 60000);
            list.AssertOk();
        }
        finally
        {
            var delete = await _client.CallToolAsync("DeleteUserState", new JsonObject
            {
                ["name"] = stateName,
            }, 60000);
            delete.AssertOk();
        }
    }

    [Fact]
    public async Task StateMachine_Lifecycle_CreateUpdateDelete()
    {
        var s1 = $"mcp_test_st1_{Guid.NewGuid():N}";
        var s2 = $"mcp_test_st2_{Guid.NewGuid():N}";
        var machineTitle = $"mcp_test_machine_{Guid.NewGuid():N}";

        var createS1 = await _client.CallToolAsync("CreateUserState", new JsonObject
        {
            ["name"] = s1,
            ["title"] = "MCP исходное",
        }, 60000);

        if (AdminGate.ShouldSkip(createS1, "StateMachine"))
            return;

        var renamedTitle = $"{machineTitle}_renamed";
        var typeName = $"mcp_test_smtype_{Guid.NewGuid():N}";
        var machineCreated = false;
        var typeCreated = false;
        try
        {
            var createS2 = await _client.CallToolAsync("CreateUserState", new JsonObject
            {
                ["name"] = s2,
                ["title"] = "MCP целевое",
            }, 60000);
            createS2.AssertOk();

            var createMachine = await _client.CallToolAsync("CreateStateMachine", new JsonObject
            {
                ["title"] = machineTitle,
                ["transitionsJson"] = $"{{\"{s1}\": [{{\"stateTo\": \"{s2}\", \"displayName\": \"Перевести\"}}]}}",
            }, 60000);
            createMachine.AssertOk();
            machineCreated = true;

            // ID машины — из сообщения "…создана (ID: <guid>)"
            var idMatch = System.Text.RegularExpressions.Regex.Match(
                createMachine.Message, @"ID: ([0-9a-fA-F-]{36})");
            Assert.True(idMatch.Success, $"Не удалось извлечь ID машины из: {createMachine.Message}");
            var machineId = idMatch.Groups[1].Value;

            var updateMachine = await _client.CallToolAsync("UpdateStateMachine", new JsonObject
            {
                ["machine"] = machineTitle,
                ["title"] = renamedTitle,
                ["transitionsJson"] = $"{{\"{s1}\": [{{\"stateTo\": \"{s2}\", \"displayName\": \"Перевести (обновлено)\"}}], \"{s2}\": [{{\"stateTo\": \"{s1}\", \"displayName\": \"Вернуть\"}}]}}",
            }, 60000);
            updateMachine.AssertOk();
            machineTitle = renamedTitle;

            // Машина видна под новым названием через обычный инструмент чтения
            var machines = await _client.CallToolAsync("GetStateMachines", null, 60000);
            machines.AssertOk();
            Assert.Contains(renamedTitle, machines.Message);

            // Состояние, используемое в машине, удалить нельзя
            var deleteUsedState = await _client.CallToolAsync("DeleteUserState", new JsonObject
            {
                ["name"] = s1,
            }, 60000);
            AdminGate.AssertErrorBecause(deleteUsedState, "используется в машинах состояний");

            // Привязка машины к типу (UpdateType stateMachineId)
            var createType = await _client.CallToolAsync("CreateType", new JsonObject
            {
                ["name"] = typeName,
                ["title"] = "MCP тип с машиной состояний",
            }, 60000);
            createType.AssertOk();
            typeCreated = true;

            var attach = await _client.CallToolAsync("UpdateType", new JsonObject
            {
                ["name"] = typeName,
                ["stateMachineId"] = machineId,
            }, 60000);
            attach.AssertOk();

            // Машину, привязанную к типу, удалить нельзя
            var deleteAttached = await _client.CallToolAsync("DeleteStateMachine", new JsonObject
            {
                ["machine"] = machineTitle,
            }, 60000);
            Assert.Equal("error", deleteAttached.Status);
            Assert.Contains(typeName, deleteAttached.Message);

            // Отвязка (stateMachineId=none)
            var detach = await _client.CallToolAsync("UpdateType", new JsonObject
            {
                ["name"] = typeName,
                ["stateMachineId"] = "none",
            }, 60000);
            detach.AssertOk();
        }
        finally
        {
            if (typeCreated)
                await _client.CallToolAsync("DeleteType", new JsonObject { ["name"] = typeName }, 60000);
            if (machineCreated)
            {
                var deleteMachine = await _client.CallToolAsync("DeleteStateMachine", new JsonObject
                {
                    ["machine"] = machineTitle,
                }, 60000);
                deleteMachine.AssertOk();
            }
            await _client.CallToolAsync("DeleteUserState", new JsonObject { ["name"] = s1 }, 60000);
            await _client.CallToolAsync("DeleteUserState", new JsonObject { ["name"] = s2 }, 60000);
        }
    }

    [Fact]
    public async Task UpdateAutomationConfig_NotFound_ReturnsError()
    {
        var result = await _client.CallToolAsync("UpdateAutomationConfig", new JsonObject
        {
            ["name"] = $"mcp_nonexistent_{Guid.NewGuid():N}",
            ["isPaused"] = "true",
        }, 60000);

        AdminGate.AssertErrorBecause(result, "не найдена");
    }

    [Fact]
    public async Task ValidateAutomationScript_ExistingAutomationCode_IsValid()
    {
        // Код реальной автоматизации из базы обязан проходить серверную валидацию
        var list = await _client.CallToolAsync("GetAutomationConfigs", null, 60000);
        list.AssertOk();
        if (list.Count == 0 || list.ResultFile == null)
        {
            Console.WriteLine("[ValidateValid] SKIP: в базе нет автоматизаций.");
            return;
        }

        var code = (await File.ReadAllLinesAsync(list.ResultFile))
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => System.Text.Json.Nodes.JsonNode.Parse(l)?["cSharpCode"]?.GetValue<string>())
            .FirstOrDefault(c => !string.IsNullOrWhiteSpace(c));
        if (code == null)
        {
            Console.WriteLine("[ValidateValid] SKIP: в базе нет автоматизаций с C#-кодом.");
            return;
        }

        var result = await _client.CallToolAsync("ValidateAutomationScript", new JsonObject
        {
            ["cSharpCode"] = code,
        }, 60000);

        if (AdminGate.ShouldSkip(result, "ValidateValid"))
            return;

        result.AssertOk();
        Assert.Contains("валиден", result.Message);
    }

    [Fact]
    public async Task ValidateAutomationScript_InvalidCode_ReturnsError()
    {
        var result = await _client.CallToolAsync("ValidateAutomationScript", new JsonObject
        {
            ["cSharpCode"] = "this is not valid c# code {{{",
        }, 60000);

        // С админ-доступом ошибка обязана быть от компиляции, а не от нехватки прав
        AdminGate.AssertErrorBecause(result, "не прошёл проверку");
    }
}
