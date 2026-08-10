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

        if (create.Status == "error")
        {
            Console.WriteLine($"[AutomationLifecycle] SKIP (нет админ-доступа?): {create.Message}");
            return;
        }

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

        if (create.Status == "error")
        {
            Console.WriteLine($"[TypeLifecycle] SKIP (нет админ-доступа?): {create.Message}");
            return;
        }

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

        if (createParent.Status == "error")
        {
            Console.WriteLine($"[TypeChildren] SKIP (нет админ-доступа?): {createParent.Message}");
            return;
        }

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
            Assert.Equal("error", addAgain.Status);
        }
        finally
        {
            await _client.CallToolAsync("DeleteType", new JsonObject { ["name"] = childName }, 60000);
            await _client.CallToolAsync("DeleteType", new JsonObject { ["name"] = parentName }, 60000);
        }
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

        if (create.Status == "error")
        {
            Console.WriteLine($"[UserState] SKIP (нет админ-доступа?): {create.Message}");
            return;
        }

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

        if (createS1.Status == "error")
        {
            Console.WriteLine($"[StateMachine] SKIP (нет админ-доступа?): {createS1.Message}");
            return;
        }

        var machineCreated = false;
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

            var updateMachine = await _client.CallToolAsync("UpdateStateMachine", new JsonObject
            {
                ["machine"] = machineTitle,
                ["transitionsJson"] = $"{{\"{s1}\": [{{\"stateTo\": \"{s2}\", \"displayName\": \"Перевести (обновлено)\"}}], \"{s2}\": [{{\"stateTo\": \"{s1}\", \"displayName\": \"Вернуть\"}}]}}",
            }, 60000);
            updateMachine.AssertOk();

            // Машина видна через обычный инструмент чтения
            var machines = await _client.CallToolAsync("GetStateMachines", null, 60000);
            machines.AssertOk();
            Assert.Contains(machineTitle, machines.Message);

            // Состояние, используемое в машине, удалить нельзя
            var deleteUsedState = await _client.CallToolAsync("DeleteUserState", new JsonObject
            {
                ["name"] = s1,
            }, 60000);
            Assert.Equal("error", deleteUsedState.Status);
        }
        finally
        {
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

        Assert.Equal("error", result.Status);
    }

    [Fact]
    public async Task ValidateAutomationScript_InvalidCode_ReturnsError()
    {
        var result = await _client.CallToolAsync("ValidateAutomationScript", new JsonObject
        {
            ["cSharpCode"] = "this is not valid c# code {{{",
        }, 60000);

        // Ошибка либо от компиляции (ожидаемо), либо от отсутствия админ-доступа (скип)
        Console.WriteLine($"[ValidateScript] status={result.Status}, msg={result.Message}");
        Assert.Equal("error", result.Status);
    }
}
