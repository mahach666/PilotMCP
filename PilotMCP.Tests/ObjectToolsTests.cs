using System.Text.Json.Nodes;

namespace PilotMCP.Tests;

[Collection("Pilot")]
public class ObjectToolsTests
{
    private readonly McpTestClient _client;
    public ObjectToolsTests(PilotFixture fixture) => _client = fixture.Client;

    [Fact]
    public async Task GetDatabaseInfo_ReturnsInfo()
    {
        var result = await _client.CallToolAsync("GetDatabaseInfo");

        result.AssertOk();
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
    }

    [Fact]
    public async Task GetObject_WithValidId_ReturnsObject()
    {
        var result = await _client.CallToolAsync("GetObject", new JsonObject
        {
            ["objectId"] = "00000001-0001-0001-0001-000000000001"
        });

        Assert.False(result.IsError, $"Expected success but got: {result.RawText}");
        Assert.False(string.IsNullOrWhiteSpace(result.RawText));
        Assert.Contains("ID:", result.RawText);
    }

    [Fact]
    public async Task GetObject_WithInvalidId_ReturnsError()
    {
        var result = await _client.CallToolAsync("GetObject", new JsonObject
        {
            ["objectId"] = "not-a-guid"
        });

        result.AssertError();
    }

    [Fact]
    public async Task GetObjectsToFile_ReturnsFile()
    {
        var result = await _client.CallToolAsync("GetObjectsToFile", new JsonObject
        {
            ["objectIds"] = "00000001-0001-0001-0001-000000000001"
        });

        result.AssertOk();
        Assert.NotNull(result.ResultFile);
    }

    [Fact]
    public async Task CreateObject_WithInvalidType_ReturnsError()
    {
        var result = await _client.CallToolAsync("CreateObject", new JsonObject
        {
            ["parentId"] = "00000001-0001-0001-0001-000000000001",
            ["typeName"] = "NonExistentType_12345",
            ["attributeName"] = "name",
            ["attributeValue"] = "test"
        });

        result.AssertError();
    }

    [Fact]
    public async Task GetObject_Brief_ReturnsSummary()
    {
        var result = await _client.CallToolAsync("GetObject", new JsonObject
        {
            ["objectId"] = "00000001-0001-0001-0001-000000000001",
            ["brief"] = true
        });

        Assert.False(result.IsError, $"Error: {result.RawText}");
        Assert.Contains("ID:", result.RawText);
        // Brief mode should not contain "Атрибуты:"
        Assert.DoesNotContain("## Атрибуты:", result.RawText);
    }

    [Fact]
    public async Task GetAttribute_OnRoot_ReturnsValue()
    {
        var result = await _client.CallToolAsync("GetAttribute", new JsonObject
        {
            ["objectId"] = "00000001-0001-0001-0001-000000000001",
            ["attributeName"] = "name"
        });

        Assert.False(result.IsError, $"Error: {result.RawText}");
    }

    [Fact]
    public async Task GetAttribute_NonExistent_ReturnsError()
    {
        var result = await _client.CallToolAsync("GetAttribute", new JsonObject
        {
            ["objectId"] = "00000001-0001-0001-0001-000000000001",
            ["attributeName"] = "nonexistent_attribute_xyz"
        });

        result.AssertError();
    }

    [Fact]
    public async Task MoveObject_WithInvalidId_ReturnsError()
    {
        var result = await _client.CallToolAsync("MoveObject", new JsonObject
        {
            ["objectId"] = "not-a-guid",
            ["newParentId"] = "00000001-0001-0001-0001-000000000001"
        });

        result.AssertError();
    }

    [Fact]
    public async Task MoveObject_ToMissingParent_ReturnsClearError()
    {
        var roots = await _client.CallToolAsync("GetRootObjects", new JsonObject());
        roots.AssertOk();
        Assert.False(string.IsNullOrEmpty(roots.ResultFile), $"Нет файла результатов: {roots.RawText}");
        var firstLine = File.ReadLines(roots.ResultFile!).FirstOrDefault();
        Assert.False(string.IsNullOrEmpty(firstLine), $"Нет корневых объектов: {roots.RawText}");
        var objectId = JsonNode.Parse(firstLine!)!["id"]!.GetValue<string>();

        var result = await _client.CallToolAsync("MoveObject", new JsonObject
        {
            ["objectId"] = objectId,
            ["newParentId"] = "11111111-2222-3333-4444-555555555555"
        });

        result.AssertError();
        Assert.Contains("не найден", result.Message);
    }

    [Fact]
    public async Task CreateObject_UnderMissingParent_ReturnsClearError()
    {
        var result = await _client.CallToolAsync("CreateObject", new JsonObject
        {
            ["parentId"] = "11111111-2222-3333-4444-555555555555",
            ["typeName"] = "document",
            ["attributeName"] = "sheet_number",
            ["attributeValue"] = "1"
        });

        result.AssertError();
        Assert.Contains("Родительский объект", result.Message);
    }

    [Fact]
    public async Task GetFileSize_WithUnknownBody_ReturnsError()
    {
        var result = await _client.CallToolAsync("GetFileSize", new JsonObject
        {
            ["fileBodyId"] = Guid.NewGuid().ToString()
        });

        // Несуществующий файл — ошибка, а не «0 байт».
        result.AssertError();
        Assert.DoesNotContain("disposed", result.RawText);
    }

    private const string Root = "00000001-0001-0001-0001-000000000001";

    [Theory]
    [InlineData("UnlockObject", "Alive")]
    [InlineData("UnfreezeObject", "Alive")]
    public async Task StateTools_RejectWrongState(string tool, string state)
    {
        // Корень в состоянии Alive: Unlock/Unfreeze не применимы и ничего не меняют.
        var result = await _client.CallToolAsync(tool, new JsonObject { ["objectId"] = Root });

        result.AssertError();
        AssertWriteGuardOrContains(result, state);
    }

    [Fact]
    public async Task SetAttribute_UnknownAttribute_ReturnsError()
    {
        var result = await _client.CallToolAsync("SetAttribute", new JsonObject
        {
            ["objectId"] = Root,
            ["attributeName"] = "no_such_attribute_xyz",
            ["attributeValue"] = "1"
        });

        result.AssertError();
        AssertWriteGuardOrContains(result, "не определён");
    }

    [Fact]
    public async Task CreateLink_NumericOrServiceType_ReturnsError()
    {
        foreach (var type in new[] { "99", "1", "Publish" })
        {
            var result = await _client.CallToolAsync("CreateLink", new JsonObject
            {
                ["objectId1"] = Root,
                ["objectId2"] = Root,
                ["relationType"] = type
            });

            result.AssertError();
            AssertWriteGuardOrContains(result, "Неизвестный тип связи");
        }
    }

    [Fact]
    public async Task GetObjectsWithRights_InvalidGuid_ReturnsError()
    {
        var result = await _client.CallToolAsync("GetObjectsWithRights", new JsonObject
        {
            ["objectIds"] = $"{Root}, typo"
        });

        result.AssertError();
        Assert.Contains("typo", result.Message);
    }

    [Fact]
    public async Task AddSignatureRequest_OnObjectWithoutFiles_ReturnsError()
    {
        var result = await _client.CallToolAsync("AddSignatureRequest", new JsonObject
        {
            ["objectId"] = Root,
            ["positionId"] = 1,
            ["role"] = "Согласующий",
            ["fileName"] = "nope.pdf"
        });

        result.AssertError();
    }

    // В режиме ReadOnly запись блокируется раньше проверки — тогда достаточно самого факта ошибки.
    private static void AssertWriteGuardOrContains(ToolCallResult result, string fragment)
    {
        if (result.Message.Contains("ReadOnly", StringComparison.OrdinalIgnoreCase)) return;
        Assert.Contains(fragment, result.Message);
    }
}
