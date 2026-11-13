using System.Text.Json.Nodes;

namespace PilotMCP.Tests;

[Collection("Pilot")]
[Trait("Category", "Integration")]
public class QueryToolsTests
{
    private readonly McpTestClient _client;
    public QueryToolsTests(PilotFixture fixture) => _client = fixture.Client;

    [Fact]
    public async Task Search_WithWildcard_ReturnsResults()
    {
        var result = await _client.CallToolAsync("Search", new JsonObject
        {
            ["query"] = "*",
            ["maxResults"] = 5
        });

        result.AssertOk();
        Assert.True(result.Count > 0, "Expected at least one search result");
    }

    [Fact]
    public async Task Search_WithNoResults_ReturnsEmpty()
    {
        var result = await _client.CallToolAsync("Search", new JsonObject
        {
            ["query"] = "zzznonexistent12345qqq"
        });

        result.AssertOk();
        Assert.Equal(0, result.Count);
    }

    [Theory]
    [InlineData("00000001-0001-0001-0001-000000000001", "корень")]
    [InlineData("88dfed13-f1a0-4735-b1fc-55a71743ea5b", "папка Проекты 2014")]
    public async Task SearchInContext_Variants(string contextId, string description)
    {
        var result = await _client.CallToolAsync("SearchInContext", new JsonObject
        {
            ["contextId"] = contextId,
            ["maxResults"] = 10000
        });

        var count = result.Count;
        var msg = result.Message ?? result.RawText;
        Console.WriteLine($"[SearchInContext {description}] contextId={contextId} => count={count}, msg={msg}");
    }

    [Fact]
    public async Task GetAllObjects_ReturnsObjects()
    {
        var result = await _client.CallToolAsync("GetAllObjects");

        var count = result.Count;
        var msg = result.Message ?? result.RawText;
        Console.WriteLine($"[GetAllObjects] count={count}, msg={msg}");

        result.AssertOk();
        Assert.True(count > 0, "Expected at least one object");
    }

    [Fact]
    public async Task GetRootObjects_ReturnsObjects()
    {
        var result = await _client.CallToolAsync("GetRootObjects");

        result.AssertOk();
        Assert.True(result.Count > 0, "Expected at least one root object");
    }

    [Fact]
    public async Task GetChildren_OfRoot_ReturnsChildren()
    {
        var result = await _client.CallToolAsync("GetChildren", new JsonObject
        {
            ["parentId"] = "00000001-0001-0001-0001-000000000001"
        });

        result.AssertOk();
    }

    [Fact]
    public async Task GetTypes_ReturnsTypes()
    {
        var result = await _client.CallToolAsync("GetTypes");

        result.AssertOk();
        Assert.NotNull(result.ResultFile);
    }

    [Fact]
    public async Task GetType_ByName_ReturnsTypeInfo()
    {
        var result = await _client.CallToolAsync("GetType", new JsonObject
        {
            ["typeName"] = "Root_object_type"
        });

        Assert.Contains("Root_object_type", result.RawText);
    }

    [Fact]
    public async Task GetPeople_ReturnsPeople()
    {
        var result = await _client.CallToolAsync("GetPeople");

        result.AssertOk();
        Assert.True(result.Count > 0, "Expected at least one person");
    }

    [Fact]
    public async Task GetOrganisationUnits_ReturnsUnits()
    {
        var result = await _client.CallToolAsync("GetOrganisationUnits");

        result.AssertOk();
    }

    [Fact]
    public async Task GetUserStates_ReturnsStates()
    {
        var result = await _client.CallToolAsync("GetUserStates");

        result.AssertOk();
        Assert.Contains("состояни", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetStateMachines_ReturnsMachines()
    {
        var result = await _client.CallToolAsync("GetStateMachines");

        result.AssertOk();
        Assert.Contains("Машины состояний", result.Message);
    }

    [Theory]
    [InlineData("Search", "context", "not-a-guid")]
    [InlineData("Search", "stateId", "not-a-guid")]
    [InlineData("Search", "createdFrom", "garbage")]
    [InlineData("Search", "attribute", "noequals")]
    [InlineData("SearchByDate", "from", "garbage")]
    [InlineData("SearchByDate", "to", "garbage")]
    [InlineData("GetChildren", "parentId", "00000000-0000-0000-0000-000000000000")]
    public async Task InvalidFilterArgument_ReturnsError(string tool, string arg, string value)
    {
        var result = await _client.CallToolAsync(tool, new JsonObject { [arg] = value });
        result.AssertError();
    }

    [Fact]
    public async Task SearchByText_WithLuceneSpecials_DoesNotFailAsSyntaxError()
    {
        var result = await _client.CallToolAsync("SearchByText", new JsonObject { ["text"] = "a:b (" });
        result.AssertOk();
        Assert.Equal(0, result.Count);
    }

    [Fact]
    public async Task SearchByDate_DateOnlyUpperBound_IsAccepted()
    {
        var result = await _client.CallToolAsync("SearchByDate", new JsonObject
        { ["from"] = "2000-01-01", ["to"] = "2100-01-01", ["maxResults"] = 3 });
        result.AssertOk();
        Assert.True(result.Count > 0);
    }
}
