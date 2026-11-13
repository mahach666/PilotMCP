using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PilotMCP.Tests;

/// <summary>
/// Интеграционные тесты замечаний. Все созданные замечания удаляются в WithCleanup (DeleteObject).
/// Тестовый документ ищется через инструменты: первый документ с .xps-файлом и словом на странице 0.
/// </summary>
[Collection("Pilot")]
public class AnnotationToolsTests
{
    private readonly McpTestClient _client;
    public AnnotationToolsTests(PilotFixture fixture) => _client = fixture.Client;

    private static (string DocId, string Word)? _testDoc;

    private async Task<(string DocId, string Word)> FindTestDocument()
    {
        if (_testDoc != null) return _testDoc.Value;

        var search = await _client.CallToolAsync("Search", new JsonObject { ["query"] = "*", ["maxResults"] = 2000 });
        search.AssertOk();
        Assert.NotNull(search.ResultFile);

        var candidates = File.ReadLines(search.ResultFile!)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => JsonNode.Parse(l)!.AsObject())
            .Where(o => (o["filesCount"]?.GetValue<int>() ?? 0) > 0)
            .Select(o => o["id"]!.GetValue<string>())
            .Take(300);

        foreach (var id in candidates)
        {
            var obj = await _client.CallToolAsync("GetObject", new JsonObject { ["objectId"] = id });
            if (obj.IsError || !obj.RawText.Contains(".xps", StringComparison.OrdinalIgnoreCase)) continue;

            var text = await _client.CallToolAsync("GetDocumentText", new JsonObject
            {
                ["documentId"] = id, ["page"] = 0, ["maxChars"] = 3000
            });
            if (text.IsError) continue;
            var body = string.Join("\n", text.Message.Split('\n').Where(l => !l.StartsWith("Страница") && !l.StartsWith("===")));
            var word = Regex.Matches(body, @"\p{L}{6,}").Select(m => m.Value).FirstOrDefault();
            if (word == null) continue;

            _testDoc = (id, word);
            return _testDoc.Value;
        }

        Assert.Fail("В тестовой базе не найден документ с .xps-файлом и текстом (6+ букв) на странице 0 — тесты замечаний не могут выполняться.");
        return default;
    }

    private async Task<string> CreateRemark(string tool, JsonObject args, List<string> created)
    {
        var result = await _client.CallToolAsync(tool, args, 60000);
        result.AssertOk();
        var m = Regex.Match(result.Message, @"ID: ([0-9a-fA-F-]{36})");
        Assert.True(m.Success, $"В ответе {tool} нет ID замечания: {result.Message}");
        created.Add(m.Groups[1].Value);
        return m.Groups[1].Value;
    }

    /// <summary>
    /// Выполняет тело теста и удаляет созданные замечания. Удаляются все, даже если часть удалений упала;
    /// ошибки удаления собираются в AggregateException вместе с исходной ошибкой теста (она идёт первой и не теряется).
    /// </summary>
    private async Task WithCleanup(Func<List<string>, Task> body)
    {
        var created = new List<string>();
        Exception? original = null;
        try { await body(created); }
        catch (Exception ex) { original = ex; }

        var errors = new List<Exception>();
        if (original != null) errors.Add(original);
        foreach (var id in created)
        {
            try { (await _client.CallToolAsync("DeleteObject", new JsonObject { ["objectId"] = id })).AssertOk(); }
            catch (Exception ex) { errors.Add(new InvalidOperationException($"Не удалось удалить замечание {id}", ex)); }
        }

        if (errors.Count == 1 && original != null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(original).Throw();
        if (errors.Count > 0)
            throw new AggregateException("Тест замечаний завершился с ошибками", errors);
    }

    private async Task<JsonArray> GetAnnotations(string docId, int dpi = 150)
    {
        var result = await _client.CallToolAsync("GetAnnotations", new JsonObject { ["documentId"] = docId, ["dpi"] = dpi }, 60000);
        result.AssertOk();
        return JsonNode.Parse(result.Message)!.AsArray();
    }

    private static bool Contains(JsonArray all, string id) => all.Any(n => n!["id"]!.GetValue<string>() == id);

    private static HashSet<string> Ids(JsonArray all) => all.Select(n => n!["id"]!.GetValue<string>()).ToHashSet();

    /// <summary>
    /// Сервер применяет изменения асинхронно: сразу после создания/удаления список замечаний может быть
    /// ещё прежним, поэтому ждём (до ~15 с), пока замечание появится или исчезнет.
    /// </summary>
    private async Task<JsonArray> WaitForRemark(string docId, string id, bool present = true)
    {
        var all = await GetAnnotations(docId);
        for (var i = 0; i < 30 && Contains(all, id) != present; i++)
        {
            await Task.Delay(500);
            all = await GetAnnotations(docId);
        }
        return all;
    }

    private static JsonObject Remark(JsonArray all, string id)
        => all.Select(n => n!.AsObject()).Single(o => o["id"]!.GetValue<string>() == id);

    private static void AssertBox(JsonObject shape, double x, double y, double w, double h)
    {
        Assert.InRange(shape["x"]!.GetValue<double>(), x - 1, x + 1);
        Assert.InRange(shape["y"]!.GetValue<double>(), y - 1, y + 1);
        Assert.InRange(shape["width"]!.GetValue<double>(), w - 1, w + 1);
        Assert.InRange(shape["height"]!.GetValue<double>(), h - 1, h + 1);
    }

    [Fact]
    public Task CreateAnnotation_Markup_RoundTripsThroughGetAnnotations() => WithCleanup(async created =>
    {
        var (docId, _) = await FindTestDocument();
        var id = await CreateRemark("CreateAnnotation", new JsonObject
        {
            ["documentId"] = docId, ["shape"] = "rectangle", ["color"] = "blue",
            ["x"] = 150, ["y"] = 200, ["width"] = 200, ["height"] = 100, ["page"] = 0,
            ["content"] = "тест markup rectangle", ["strokeWidth"] = 3
        }, created);

        var remark = Remark(await WaitForRemark(docId, id), id);
        Assert.Equal("markup", remark["format"]!.GetValue<string>());
        Assert.Equal("тест markup rectangle", remark["text"]!.GetValue<string>());
        Assert.True(remark["markupVersionIsCurrent"]!.GetValue<bool>());
        Assert.False(string.IsNullOrEmpty(remark["creator"]!.GetValue<string>()));
        Assert.Null(remark["error"]);
        var shape = Assert.Single(remark["shapes"]!.AsArray())!.AsObject();
        Assert.Equal("Rectangle", shape["subtype"]!.GetValue<string>());
        Assert.Equal(0, shape["page"]!.GetValue<int>());
        AssertBox(shape, 150, 200, 200, 100);
        Assert.Equal("#0000FF", shape["color"]!.GetValue<string>());
        Assert.Equal(3, shape["strokeWidth"]!.GetValue<double>());

        // На сервере: атрибуты нового формата и нет legacy-атрибута "annotation"
        var obj = await _client.CallToolAsync("GetObject", new JsonObject { ["objectId"] = id });
        obj.AssertOk();
        Assert.Contains("  markups = ", obj.RawText);
        Assert.Contains("  markups_version = ", obj.RawText);
        Assert.DoesNotContain("  annotation = ", obj.RawText);
    });

    [Fact]
    public Task CreateMarkupAnnotation_SeveralShapes_AreOneRemark() => WithCleanup(async created =>
    {
        var (docId, _) = await FindTestDocument();
        var shapes = new JsonArray
        {
            new JsonObject { ["shape"] = "ellipse", ["x"] = 400, ["y"] = 400, ["width"] = 100, ["height"] = 60 },
            new JsonObject { ["shape"] = "arrow", ["x"] = 300, ["y"] = 300, ["width"] = 120, ["height"] = 80, ["color"] = "black" },
            new JsonObject { ["shape"] = "triangle", ["x"] = 100, ["y"] = 500, ["width"] = 80, ["height"] = 60 },
            new JsonObject { ["shape"] = "polygon", ["points"] = "100,100;200,100;200,180;100,180", ["cloud"] = true },
            new JsonObject { ["shape"] = "polyline", ["points"] = "50,50;80,90;120,60" },
            new JsonObject { ["shape"] = "ink", ["points"] = "10,10;20,25;30,15;40,40" },
            new JsonObject { ["shape"] = "point", ["x"] = 700, ["y"] = 700 },
        };
        var id = await CreateRemark("CreateMarkupAnnotation", new JsonObject
        {
            ["documentId"] = docId, ["shapesJson"] = shapes.ToJsonString(), ["content"] = "несколько фигур"
        }, created);

        var all = await WaitForRemark(docId, id);
        Assert.Single(all, n => n!["id"]!.GetValue<string>() == id);
        var remark = Remark(all, id);
        Assert.Equal("markup", remark["format"]!.GetValue<string>());
        var read = remark["shapes"]!.AsArray().Select(n => n!.AsObject()).ToList();
        Assert.Equal(
            new[] { "Ellipse", "Line", "Polygon", "Polygon", "Polyline", "Ink", "Text" },
            read.Select(s => s["subtype"]!.GetValue<string>()).ToArray());

        AssertBox(read[0], 350, 370, 100, 60);   // эллипс: (x, y) — центр
        AssertBox(read[1], 300, 300, 120, 80);   // стрелка: остриё (300,300), хвост (420,380)
        AssertBox(read[3], 100, 100, 100, 80);   // polygon
        AssertBox(read[4], 50, 50, 70, 40);      // polyline
        Assert.Equal("#FF000000", read[1]["color"]!.GetValue<string>()); // чёрный хранится с альфой, иначе 0 = прозрачный
    });

    [Theory]
    [InlineData("highlight", "Highlight")]
    [InlineData("underline", "Underline")]
    [InlineData("strikeout", "StrikeOut")]
    public Task AnnotateText_Markup_CreatesTextDecoration(string style, string subtype) => WithCleanup(async created =>
    {
        var (docId, word) = await FindTestDocument();
        var id = await CreateRemark("AnnotateText", new JsonObject
        {
            ["documentId"] = docId, ["searchText"] = word, ["comment"] = $"тест {style}", ["page"] = 0, ["style"] = style
        }, created);

        var remark = Remark(await WaitForRemark(docId, id), id);
        Assert.Equal("markup", remark["format"]!.GetValue<string>());
        Assert.Equal($"тест {style}", remark["text"]!.GetValue<string>());
        var shape = Assert.Single(remark["shapes"]!.AsArray())!.AsObject();
        Assert.Equal(subtype, shape["subtype"]!.GetValue<string>());
        Assert.True(shape["width"]!.GetValue<double>() > 0);
        Assert.True(shape["height"]!.GetValue<double>() > 0);
        Assert.True(shape["y"]!.GetValue<double>() >= 0);
    });

    [Fact]
    public Task Legacy_Format_StillWorks() => WithCleanup(async created =>
    {
        var (docId, word) = await FindTestDocument();
        var textId = await CreateRemark("AnnotateText", new JsonObject
        {
            ["documentId"] = docId, ["searchText"] = word, ["comment"] = "тест legacy text", ["page"] = 0, ["format"] = "legacy"
        }, created);
        var shapeId = await CreateRemark("CreateAnnotation", new JsonObject
        {
            ["documentId"] = docId, ["shape"] = "rectangle", ["color"] = "red",
            ["x"] = 150, ["y"] = 150, ["width"] = 200, ["height"] = 100, ["page"] = 0,
            ["content"] = "тест legacy rectangle", ["format"] = "legacy"
        }, created);

        await WaitForRemark(docId, textId);
        var all = await WaitForRemark(docId, shapeId);
        var text = Remark(all, textId);
        Assert.Equal("legacy", text["format"]!.GetValue<string>());
        Assert.Equal("TextStickyNote", Assert.Single(text["shapes"]!.AsArray())!["kind"]!.GetValue<string>());
        var rect = Remark(all, shapeId);
        Assert.Equal("legacy", rect["format"]!.GetValue<string>());
        Assert.Equal("RedPencil", Assert.Single(rect["shapes"]!.AsArray())!["kind"]!.GetValue<string>());

        var obj = await _client.CallToolAsync("GetObject", new JsonObject { ["objectId"] = shapeId });
        Assert.Contains("  annotation = ", obj.RawText);
        Assert.DoesNotContain("  markups = ", obj.RawText);
    });

    [Fact]
    public Task GetAnnotations_SkipsRemarksInRecycleBin() => WithCleanup(async created =>
    {
        var (docId, _) = await FindTestDocument();
        var id = await CreateRemark("CreateAnnotation", new JsonObject
        {
            ["documentId"] = docId, ["shape"] = "point", ["color"] = "red", ["x"] = 120, ["y"] = 120
        }, created);
        Assert.True(Contains(await WaitForRemark(docId, id), id));

        (await _client.CallToolAsync("DeleteObject", new JsonObject { ["objectId"] = id })).AssertOk();
        created.Remove(id); // уже в корзине
        // Сравниваем по ID (а не по количеству): параллельно могут исчезать замечания других тестов.
        Assert.DoesNotContain(id, Ids(await WaitForRemark(docId, id, present: false)));
    });

    [Fact]
    public Task InvalidInput_ReturnsErrors() => WithCleanup(async created =>
    {
        var (docId, word) = await FindTestDocument();

        async Task ExpectError(string tool, JsonObject args)
        {
            var result = await _client.CallToolAsync(tool, args, 60000);
            result.AssertError();
            // ошибка не должна оставлять замечание
            var m = Regex.Match(result.Message, @"ID: ([0-9a-fA-F-]{36})");
            if (m.Success) created.Add(m.Groups[1].Value);
        }

        Task ExpectShapes(string shapesJson)
            => ExpectError("CreateMarkupAnnotation", new() { ["documentId"] = docId, ["shapesJson"] = shapesJson });

        var before = Ids(await GetAnnotations(docId));

        await ExpectShapes("не json");
        await ExpectShapes("[]");
        await ExpectShapes("[null]");
        await ExpectShapes("[{\"shape\":\"hexagon\"}]");
        await ExpectShapes("[{\"shape\":\"freetext\",\"width\":150,\"height\":40}]");
        await ExpectShapes("[{\"shape\":\"rectangle\",\"colour\":\"red\"}]");
        await ExpectShapes("[{\"shape\":\"rectangle\",\"fillColor\":\"yellow\"}]");
        await ExpectShapes("[{\"shape\":\"rectangle\",\"lineStyle\":\"dashed\"}]");
        await ExpectShapes("[{\"shape\":\"rectangle\",\"text\":\"t\"}]");
        await ExpectShapes("[{\"shape\":\"polygon\",\"points\":\"1,1;2,2\"}]");
        await ExpectShapes("[{\"shape\":\"polyline\",\"points\":\"1,1;a,b\"}]");
        await ExpectShapes("[{\"shape\":\"rectangle\",\"cloud\":true}]");
        await ExpectShapes("[{\"shape\":\"arrow\",\"endEnding\":\"closedarrow\"}]");
        await ExpectShapes("[{\"shape\":\"rectangle\",\"color\":\"notacolor\"}]");
        await ExpectShapes("[{\"shape\":\"rectangle\",\"color\":\"#GGGGGG\"}]");
        await ExpectShapes("[{\"shape\":\"rectangle\",\"width\":0}]");
        await ExpectShapes("[{\"shape\":null}]");
        await ExpectShapes("[{\"shape\":\"polygon\",\"points\":null}]");
        await ExpectShapes("[{\"shape\":\"rectangle\",\"page\":9999}]");
        await ExpectError("CreateMarkupAnnotation", new() { ["documentId"] = Guid.NewGuid().ToString(), ["shapesJson"] = "[{\"shape\":\"rectangle\"}]" });
        await ExpectError("CreateMarkupAnnotation", new() { ["documentId"] = "не-guid", ["shapesJson"] = "[{\"shape\":\"rectangle\"}]" });

        await ExpectError("CreateAnnotation", new() { ["documentId"] = docId, ["shape"] = "rectangle", ["color"] = "red", ["x"] = 1, ["y"] = 1, ["format"] = "xml" });
        await ExpectError("CreateAnnotation", new() { ["documentId"] = docId, ["shape"] = "freetext", ["color"] = "red", ["x"] = 1, ["y"] = 1 });
        await ExpectError("CreateAnnotation", new() { ["documentId"] = docId, ["shape"] = "arrow", ["color"] = "red", ["x"] = 1, ["y"] = 1, ["endEnding"] = "closedarrow" });
        await ExpectError("CreateAnnotation", new() { ["documentId"] = docId, ["shape"] = "polygon", ["color"] = "red", ["x"] = 1, ["y"] = 1, ["points"] = "1,1;2,2;3,1", ["format"] = "legacy" });
        await ExpectError("AnnotateText", new() { ["documentId"] = docId, ["searchText"] = word, ["comment"] = "c", ["page"] = 0, ["style"] = "bold" });
        await ExpectError("AnnotateText", new() { ["documentId"] = docId, ["searchText"] = word, ["comment"] = "c", ["page"] = 0, ["color"] = "notacolor" });
        await ExpectError("AnnotateText", new() { ["documentId"] = docId, ["searchText"] = "x", ["comment"] = "c", ["format"] = "xml" });
        await ExpectError("GetAnnotations", new() { ["documentId"] = Guid.NewGuid().ToString() });
        await ExpectError("GetAnnotations", new() { ["documentId"] = "не-guid" });

        // Новых замечаний не появилось (исчезнуть могут только удалённые другими тестами).
        Assert.Empty(Ids(await GetAnnotations(docId)).Except(before));
    });
}
