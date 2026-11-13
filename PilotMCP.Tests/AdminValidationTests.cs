using System.Text.Json.Nodes;

namespace PilotMCP.Tests;

/// <summary>Проверки входа админских инструментов: ошибка возвращается до обращения к данным сервера.</summary>
[Collection("Pilot")]
public class AdminValidationTests
{
    private readonly McpTestClient _client;
    public AdminValidationTests(PilotFixture fixture) => _client = fixture.Client;

    [Fact]
    public async Task ChangeSettings_WithoutTarget_ReturnsError()
    {
        var r = await _client.CallToolAsync("ChangeSettings", new JsonObject { ["key"] = "mcp_test_key", ["value"] = "x" });
        r.AssertError();
    }

    [Fact]
    public async Task UpdatePerson_WithoutFields_ReturnsError()
    {
        var r = await _client.CallToolAsync("UpdatePerson", new JsonObject { ["personId"] = 1 });
        r.AssertError();
    }

    [Fact]
    public async Task UpdatePerson_WithBadBool_ReturnsError()
    {
        var r = await _client.CallToolAsync("UpdatePerson", new JsonObject { ["personId"] = 1, ["isInactive"] = "yes" });
        r.AssertError();
    }

    [Theory]
    [InlineData("CreateType", "kind", "99")]
    [InlineData("CreateUserState", "color", "42")]
    public async Task NumericEnumString_IsRejected(string tool, string param, string value)
    {
        var args = new JsonObject { ["name"] = $"mcp_test_{Guid.NewGuid():N}", [param] = value };
        if (tool == "CreateType") args["title"] = "t";
        if (tool == "CreateUserState") args["title"] = "t";
        var r = await _client.CallToolAsync(tool, args, 60000);
        r.AssertError();
    }
}
