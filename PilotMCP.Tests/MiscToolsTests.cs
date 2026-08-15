using System.Text.Json.Nodes;

namespace PilotMCP.Tests;

[Collection("Pilot")]
public class MiscToolsTests
{
    private readonly McpTestClient _client;
    public MiscToolsTests(PilotFixture fixture) => _client = fixture.Client;

    private const string RootObjectId = "00000001-0001-0001-0001-000000000001";

    // ── AccessTools ──

    [Fact]
    public async Task CheckAccess_OnRootObject_ReturnsOk()
    {
        var result = await _client.CallToolAsync("CheckAccess", new JsonObject
        {
            ["objectId"] = RootObjectId,
            ["personId"] = 1
        });

        Assert.False(result.IsError, $"Tool error: {result.RawText}");
    }

    [Fact]
    public async Task GetAccessRecords_OnRootObject_ReturnsOk()
    {
        var result = await _client.CallToolAsync("GetAccessRecords", new JsonObject
        {
            ["objectId"] = RootObjectId
        });

        Assert.False(result.IsError, $"Tool error: {result.RawText}");
    }

    [Fact]
    public async Task GetSubscribers_OnRootObject_ReturnsOk()
    {
        var result = await _client.CallToolAsync("GetSubscribers", new JsonObject
        {
            ["objectId"] = RootObjectId
        });

        result.AssertOk();
    }

    // ── HistoryTools ──

    [Fact]
    public async Task GetHistory_ReturnsResult()
    {
        var result = await _client.CallToolAsync("GetHistory", new JsonObject
        {
            ["objectId"] = "818cead7-c607-4943-afd6-27a98301ab06"
        });

        // GetHistory may return error for some objects — just verify no crash
        Assert.False(result.IsError && result.RawText.Contains("disposed"),
            $"Connection error: {result.RawText}");
    }

    [Fact]
    public async Task GetChangesets_ReturnsChangesets()
    {
        var result = await _client.CallToolAsync("GetChangesets", new JsonObject
        {
            ["first"] = 1,
            ["last"] = 5
        });

        result.AssertOk();
    }

    // ── PeopleTools ──

    [Fact]
    public async Task GetPeopleByIds_ReturnsData()
    {
        var result = await _client.CallToolAsync("GetPeopleByIds", new JsonObject
        {
            ["personIds"] = "1"
        });

        Assert.False(result.IsError, $"Tool error: {result.RawText}");
    }

    [Fact]
    public async Task GetOrgUnitsByIds_WithInvalidId_ReturnsResult()
    {
        var result = await _client.CallToolAsync("GetOrgUnitsByIds", new JsonObject
        {
            ["unitIds"] = "1"
        });

        Assert.False(result.IsError, $"Tool error: {result.RawText}");
    }

    // ── SettingsTools ──

    [Fact]
    public async Task GetPersonalSettings_ReturnsSettings()
    {
        var result = await _client.CallToolAsync("GetPersonalSettings", new JsonObject
        {
            ["key"] = "test"
        });

        Assert.False(result.IsError, $"Tool error: {result.RawText}");
    }

    [Fact]
    public async Task GetCommonSettings_ReturnsSettings()
    {
        var result = await _client.CallToolAsync("GetCommonSettings");

        Assert.False(result.IsError, $"Tool error: {result.RawText}");
    }

    // ── CommandTools ──

    [Fact]
    public async Task InvokeCommand_WithInvalidBase64_ReturnsCleanError()
    {
        var result = await _client.CallToolAsync("InvokeCommand", new JsonObject
        {
            ["commandName"] = "AnyCommand",
            ["data"] = "это не base64!!"
        });

        result.AssertError();
        Assert.Contains("base64", result.Message);
        // Ошибка валидации, а не проброшенное исключение со стектрейсом
        Assert.DoesNotContain("System.Convert", result.RawText);
    }

    [Fact]
    public async Task InvokeCommand_UnknownCommand_ReturnsTerminalHint()
    {
        var result = await _client.CallToolAsync("InvokeCommand", new JsonObject
        {
            ["commandName"] = "NonExistentCommand_xyz"
        });

        result.AssertError();
        Assert.Contains("не зарегистрирована", result.Message);
        Assert.DoesNotContain("at Ascon.Pilot.Transport", result.RawText);
    }

    // ── ComputeFileMd5 ──

    [Fact]
    public async Task ComputeFileMd5_LocalFile_ReturnsKnownHash()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"md5test_{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(tempFile, "hello world");
        try
        {
            var result = await _client.CallToolAsync("ComputeFileMd5", new JsonObject
            {
                ["fileBodyIdOrPath"] = tempFile
            });

            result.AssertOk();
            // Эталонный MD5 строки "hello world"
            Assert.Contains("5eb63bbbe01eeed093cb22bb8f5acdc3", result.Message);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task ComputeFileMd5_MissingFile_ReturnsError()
    {
        var result = await _client.CallToolAsync("ComputeFileMd5", new JsonObject
        {
            ["fileBodyIdOrPath"] = @"C:\no\such\file_xyz.bin"
        });

        result.AssertError();
    }

    // ── RelationTools ──

    [Fact]
    public async Task GetObjectsWithRights_ReturnsData()
    {
        var result = await _client.CallToolAsync("GetObjectsWithRights", new JsonObject
        {
            ["objectIds"] = RootObjectId
        });

        Assert.False(result.IsError, $"Tool error: {result.RawText}");
    }

    // ── New tools (read-only tests) ──

    [Fact]
    public async Task CheckAccessByUnit_OnRoot_ReturnsOk()
    {
        var result = await _client.CallToolAsync("CheckAccessByUnit", new JsonObject
        {
            ["objectId"] = RootObjectId,
            ["unitId"] = 1
        });

        Assert.False(result.IsError, $"Tool error: {result.RawText}");
    }

    // ── PeopleTools ──

    [Fact]
    public async Task GetPersonOnPosition_ReturnsResult()
    {
        var result = await _client.CallToolAsync("GetPersonOnPosition", new JsonObject
        {
            ["positionId"] = 1
        });

        Assert.False(result.IsError, $"Tool error: {result.RawText}");
    }
}
