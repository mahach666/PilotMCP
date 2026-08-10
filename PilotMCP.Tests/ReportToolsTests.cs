using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PilotMCP.Tests;

[Collection("Pilot")]
public class ReportToolsTests
{
    private readonly McpTestClient _client;
    public ReportToolsTests(PilotFixture fixture) => _client = fixture.Client;

    [Fact]
    public async Task FindReports_FindsReportFolder()
    {
        var result = await _client.CallToolAsync("FindReports", timeoutMs: 30000);

        result.AssertOk();
        Assert.True(
            result.Message.Contains("Папка отчётов") ||
            Regex.IsMatch(result.Message, @"[0-9a-fA-F\-]{36}"),
            "Expected report folder info in response");
    }

    [Fact]
    public async Task GetReport_WithKnownReport()
    {
        var findResult = await _client.CallToolAsync("FindReports", timeoutMs: 30000);
        findResult.AssertOk();

        // Parse a report GUID from lines like "  - <guid>: <name> [.repx]"
        var match = Regex.Match(findResult.Message,
            @"- ([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}):");

        if (!match.Success)
            return; // No reports in database — skip

        var reportId = match.Groups[1].Value;

        var result = await _client.CallToolAsync("GetReport", new JsonObject
        {
            ["objectId"] = reportId
        });

        result.AssertOk();

        // Отчёт больше 15000 символов инструмент не отдаёт текстом, а сохраняет в файл —
        // тогда XML проверяем в нём.
        var savedPath = Regex.Match(result.Message, @"сохранён: (.+\.repx)").Groups[1].Value.Trim();
        var content = savedPath.Length > 0
            ? await File.ReadAllTextAsync(savedPath)
            : result.RawText;

        Assert.True(
            content.Contains("<?xml") || content.Contains("XtraReportsLayoutSerializer"),
            $"Expected XML report content, got: {result.Message[..Math.Min(300, result.Message.Length)]}");
    }

    [Fact]
    public async Task GetReport_WithInvalidId_ReturnsError()
    {
        var result = await _client.CallToolAsync("GetReport", new JsonObject
        {
            ["objectId"] = Guid.NewGuid().ToString()
        });

        result.AssertError();
    }

    [Fact]
    public async Task ListReportSamples_ReturnsSamples()
    {
        var result = await _client.CallToolAsync("ListReportSamples");

        result.AssertOk();
        Assert.True(
            result.Message.Contains("примеры") || result.Message.Contains("Доступные"),
            "Expected sample listing info");
    }

    [Fact]
    public async Task GetReportSample_WithValidName()
    {
        var listResult = await _client.CallToolAsync("ListReportSamples");
        listResult.AssertOk();

        // Parse a sample name from lines like "  - SampleName"
        var match = Regex.Match(listResult.Message, @"^\s+-\s+(.+)$", RegexOptions.Multiline);
        Assert.True(match.Success, "Expected at least one sample in listing");

        var sampleName = match.Groups[1].Value.Trim();

        var result = await _client.CallToolAsync("GetReportSample", new JsonObject
        {
            ["sampleName"] = sampleName
        });

        result.AssertOk();
        Assert.True(
            result.RawText.Contains("XtraReportsLayoutSerializer") || result.RawText.Contains(".repx"),
            "Expected report XML content");
    }

    [Fact]
    public async Task GetReportSample_WithInvalidName_ReturnsError()
    {
        var result = await _client.CallToolAsync("GetReportSample", new JsonObject
        {
            ["sampleName"] = "nonexistent_report_xyz"
        });

        result.AssertError();
    }
}
