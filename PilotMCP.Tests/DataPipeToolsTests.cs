using System.Text.Json.Nodes;

namespace PilotMCP.Tests;

[Collection("Pilot")]
public class DataPipeToolsTests
{
    private readonly McpTestClient _client;
    public DataPipeToolsTests(PilotFixture fixture) => _client = fixture.Client;

    private async Task<string> GetSearchResultFile()
    {
        var search = await _client.CallToolAsync("Search", new JsonObject
        {
            ["query"] = "*",
            ["maxResults"] = 3
        });
        search.AssertOk();
        Assert.NotNull(search.ResultFile);
        return search.ResultFile!;
    }

    [Fact]
    public async Task ExtractFields_FromSearchResults()
    {
        var resultFile = await GetSearchResultFile();

        var result = await _client.CallToolAsync("ExtractFields", new JsonObject
        {
            ["inputFile"] = resultFile,
            ["fields"] = "id,typeName"
        });

        result.AssertOk();
    }

    [Fact]
    public async Task FilterObjects_ByType()
    {
        var resultFile = await GetSearchResultFile();

        // Preview the file first to find a type name to filter by
        var preview = await _client.CallToolAsync("PreviewFile", new JsonObject
        {
            ["inputFile"] = resultFile,
            ["take"] = 1
        });

        var result = await _client.CallToolAsync("FilterObjects", new JsonObject
        {
            ["inputFile"] = resultFile,
            ["field"] = "typeName",
            ["value"] = ""
        });

        result.AssertOk();
    }

    [Fact]
    public async Task PreviewFile_ShowsContent()
    {
        var resultFile = await GetSearchResultFile();

        var result = await _client.CallToolAsync("PreviewFile", new JsonObject
        {
            ["inputFile"] = resultFile
        });

        Assert.False(result.IsError, $"PreviewFile returned error: {result.RawText}");
        Assert.NotEmpty(result.RawText);
    }

    [Fact]
    public async Task CountFile_ReturnsCount()
    {
        var resultFile = await GetSearchResultFile();

        var result = await _client.CallToolAsync("CountFile", new JsonObject
        {
            ["inputFile"] = resultFile
        });

        result.AssertOk();
        Assert.Matches(@"\d+", result.RawText);
    }

    [Fact]
    public async Task ExportCsv_WritesFile()
    {
        var resultFile = await GetSearchResultFile();
        var csvPath = Path.Combine(Path.GetTempPath(), $"test_export_{Guid.NewGuid():N}.csv");

        try
        {
            var result = await _client.CallToolAsync("ExportCsv", new JsonObject
            {
                ["inputFile"] = resultFile,
                ["outputFile"] = csvPath,
                ["columns"] = "id,typeName"
            });

            result.AssertOk();
        }
        finally
        {
            if (File.Exists(csvPath))
                File.Delete(csvPath);
        }
    }

    private static string TempJsonl(params string[] lines)
    {
        var path = Path.Combine(Path.GetTempPath(), $"pipe_{Guid.NewGuid():N}.jsonl");
        File.WriteAllLines(path, lines);
        return path;
    }

    [Fact]
    public async Task Writers_SameInputAndOutput_ReturnErrorAndKeepInput()
    {
        var input = TempJsonl("{\"a\":1,\"b\":\"x\"}", "{\"a\":2,\"b\":\"y\"}");
        var before = File.ReadAllText(input);
        try
        {
            foreach (var (tool, extra) in new[]
            {
                ("ExtractFields", new JsonObject { ["fields"] = "a" }),
                ("FilterObjects", new JsonObject { ["field"] = "a", ["value"] = "1" }),
                ("ExtractFieldToText", new JsonObject { ["field"] = "a" }),
                ("ExportCsv", new JsonObject()),
            })
            {
                extra["inputFile"] = input;
                extra["outputFile"] = input;
                (await _client.CallToolAsync(tool, extra)).AssertError();
                Assert.Equal(before, File.ReadAllText(input));
            }
        }
        finally { File.Delete(input); }
    }

    [Fact]
    public async Task ExportCsv_UnionOfKeysAndQuotedHeader_AndNewOutputDir()
    {
        var input = TempJsonl("{\"a\":1}", "{\"a\":2,\"b;c\":\"x\\ry\"}");
        var dir = Path.Combine(Path.GetTempPath(), $"csv_{Guid.NewGuid():N}");
        var output = Path.Combine(dir, "out.csv");
        try
        {
            (await _client.CallToolAsync("ExportCsv", new JsonObject { ["inputFile"] = input, ["outputFile"] = output })).AssertOk();
            var text = File.ReadAllText(output);
            Assert.Contains("a;\"b;c\"", text);
            Assert.Contains("2;\"x\ry\"", text);
        }
        finally
        {
            File.Delete(input);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task ExtractFields_NotJsonl_ReturnsErrorAndNoOutputFile()
    {
        var input = Path.Combine(Path.GetTempPath(), $"notjsonl_{Guid.NewGuid():N}.txt");
        var output = Path.Combine(Path.GetTempPath(), $"notjsonl_out_{Guid.NewGuid():N}.jsonl");
        File.WriteAllText(input, "plain text, not json");
        try
        {
            (await _client.CallToolAsync("ExtractFields", new JsonObject
            { ["inputFile"] = input, ["fields"] = "a", ["outputFile"] = output })).AssertError();
            Assert.False(File.Exists(output));
        }
        finally { File.Delete(input); }
    }

    [Fact]
    public async Task ReadFileAndPreviewFile_NegativeOffsets_AreClamped()
    {
        var input = TempJsonl("{\"a\":1}", "{\"a\":2}");
        try
        {
            var read = await _client.CallToolAsync("ReadFile", new JsonObject { ["filePath"] = input, ["offset"] = -5 });
            read.AssertOk();
            Assert.Contains("\"a\":1", read.Message);
            var preview = await _client.CallToolAsync("PreviewFile", new JsonObject { ["inputFile"] = input, ["skip"] = -3, ["take"] = 1 });
            Assert.False(preview.IsError);
            Assert.Contains("Показано 1", preview.RawText);
            var past = await _client.CallToolAsync("PreviewFile", new JsonObject { ["inputFile"] = input, ["skip"] = 50 });
            Assert.Contains("за пределами", past.RawText);
        }
        finally { File.Delete(input); }
    }
}
