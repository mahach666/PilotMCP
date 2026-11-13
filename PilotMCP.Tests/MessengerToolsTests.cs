using System.Text.Json.Nodes;

namespace PilotMCP.Tests;

[Collection("Pilot")]
[Trait("Category", "Integration")]
public class MessengerToolsTests
{
    private readonly McpTestClient _client;
    public MessengerToolsTests(PilotFixture fixture) => _client = fixture.Client;

    [Fact]
    public async Task GetChats_ReturnsChats()
    {
        var result = await _client.CallToolAsync("GetChats", new JsonObject
        {
            ["count"] = 5
        });

        result.AssertOk();
    }

    [Fact]
    public async Task GetChat_WithUnknownId_ReturnsError()
    {
        var result = await _client.CallToolAsync("GetChat", new JsonObject
        {
            ["chatId"] = Guid.NewGuid().ToString()
        });

        result.AssertError();
    }

    [Fact]
    public async Task GetPersonalChat_WithoutChat_ReturnsErrorOrChat()
    {
        // чата может не быть (тогда ошибка), но нулевой GUID как «успех» отдаваться не должен
        var result = await _client.CallToolAsync("GetPersonalChat", new JsonObject { ["personId"] = 999999 });

        if (!result.IsError)
            Assert.DoesNotContain("00000000-0000-0000-0000-000000000000", result.RawText);
    }

    [Fact]
    public async Task SearchMessages_WithInvalidChatGuid_ReturnsError()
    {
        var result = await _client.CallToolAsync("SearchMessages", new JsonObject
        {
            ["query"] = "test",
            ["chatIds"] = "not-a-guid"
        });

        result.AssertError();
    }

    [Fact]
    public async Task GetMessages_WithNegativeSkip_ReturnsError()
    {
        var result = await _client.CallToolAsync("GetMessages", new JsonObject
        {
            ["chatId"] = Guid.NewGuid().ToString(),
            ["skip"] = -1
        });

        result.AssertError();
    }

    [Theory]
    [InlineData("deadline", "01.12.2026")]
    [InlineData("attachmentIds", "not-a-guid")]
    [InlineData("attributes", "{name: x}")]
    public async Task CreateTask_WithInvalidArguments_ReturnsError(string param, string value)
    {
        var result = await _client.CallToolAsync("CreateTask", new JsonObject
        {
            ["typeName"] = "task_simple",
            [param] = value
        });

        result.AssertError();
    }

    [Fact]
    public async Task CheckOnline_WithValidPersonId()
    {
        var result = await _client.CallToolAsync("CheckOnline", new JsonObject
        {
            ["personId"] = 1
        });

        result.AssertOk();
    }

    [Fact]
    public async Task SearchMessages_ReturnsResult()
    {
        var result = await _client.CallToolAsync("SearchMessages", new JsonObject
        {
            ["query"] = "test"
        });

        // SearchMessages may return error if no messages API or empty results
        Assert.False(result.IsError, $"Tool call failed: {result.RawText}");
    }

    [Fact]
    public async Task GetMessages_WithInvalidChatId_ReturnsResult()
    {
        // May timeout or return empty — just verify no crash
        var randomId = Guid.NewGuid().ToString();
        var result = await _client.CallToolAsync("GetMessages", new JsonObject
        {
            ["chatId"] = randomId
        });

        Assert.NotNull(result.RawText);
    }
}
