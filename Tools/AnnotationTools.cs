using Ascon.Pilot.Common.Annotations;
using Ascon.Pilot.DataClasses;
using Markup = Ascon.Pilot.DataClasses.Markups.Markup;
using MarkupVersion = Ascon.Pilot.DataClasses.Markups.MarkupVersion;
using Ascon.Pilot.DataModifier.Objects;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;

namespace PilotMCP;

// =====================================================
// Annotation-инструменты (создание и чтение замечаний)
// =====================================================

[McpServerToolType]
public class AnnotationTools
{
    private readonly PilotConnection _connection;
    public AnnotationTools(PilotConnection connection) => _connection = connection;

    private string DownloadFile(Guid bodyId, string ext)
        => Helpers.DownloadToTemp(_connection.FileApi, bodyId, ext);

    [McpServerTool, Description(
        "Создать замечание к тексту на документе. Работает с любыми документами (PDF, XPS). " +
        "Автоматически находит слово/фразу и выделяет её. " +
        "Используйте для рецензирования и пометки ошибок. " +
        "По умолчанию создаёт замечание в формате Markup (клиент Pilot 26.20+): выделение, подчёркивание или зачёркивание. " +
        "format=legacy — старый формат (текстовая заметка) для серверов и клиентов старше 26.20. " +
        "Пример: searchText='квартыры', comment='Опечатка: должно быть квартиры'")]
    public Task<string> AnnotateText(
        [Description("GUID документа")] string documentId,
        [Description("Слово или фраза из документа для выделения (копируйте из текста документа, включая опечатки)")] string searchText,
        [Description("Пояснение к замечанию (что не так, как исправить)")] string comment,
        [Description("Номер страницы (с 0). Если -1, ищет на всех страницах.")] int page = -1,
        [Description("Цвет выделения")] string color = "red",
        [Description("Какое вхождение использовать, если найдено несколько (с 0)")] int occurrenceIndex = 0,
        [Description("Вид выделения (только format=markup): highlight, underline, strikeout")] string style = "highlight",
        [Description("Формат замечания: markup (клиент Pilot 26.20+) или legacy (старый, до 26.20)")] string format = "markup")
    {
        return ToolRunner.RunWriteAsync(_connection, "AnnotateText",
            $"doc={documentId}, text={searchText}, comment={comment}, page={page}, style={style}, format={format}", async () =>
        {
            if (!Guid.TryParse(documentId, out var docGuid))
                return ToolResult.Error($"Некорректный GUID: {documentId}");

            var fmt = format.Trim().ToLowerInvariant();
            if (fmt is not ("markup" or "legacy"))
                return ToolResult.Error($"Неизвестный format: {format}. Используйте: markup, legacy");
            if (fmt == "legacy" && !style.Trim().Equals("highlight", StringComparison.OrdinalIgnoreCase))
                return ToolResult.Error("Параметр style поддерживается только в format=markup.");

            var objects = await _connection.ServerApi.GetObjectsAsync(new[] { docGuid });
            if (objects == null || objects.Count == 0)
                return ToolResult.Error($"Документ {documentId} не найден.");

            var doc = objects[0];

            var xpsFile = doc.ActualFileSnapshot.Files
                .FirstOrDefault(f => f.Name.EndsWith(".xps", StringComparison.OrdinalIgnoreCase));
            var pdfFile = doc.ActualFileSnapshot.Files
                .FirstOrDefault(f => f.Name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase));

            if (xpsFile == null && pdfFile == null)
                return ToolResult.Error("Нет XPS или PDF файла.");

            List<XpsTextExtractor.TextBlock> results;
            Dictionary<int, (double Width, double Height)>? sizes = null;
            XpsTextExtractor.TextBlock match;

            // Файл скачивается один раз: по нему и поиск текста, и размеры страниц (тот же выбор XPS/PDF, что в LoadPageSizes).
            var ext = xpsFile != null ? ".xps" : ".pdf";
            var tempPath = DownloadFile((xpsFile ?? pdfFile)!.Body.Id, ext);
            try
            {
                if (xpsFile != null)
                {
                    results = XpsTextExtractor.FindText(tempPath, searchText);
                }
                else
                {
                    var pdfBlocks = PdfTextExtractor.ExtractTextBlocks(tempPath);

                    const double scale = 96.0 / 72.0;
                    results = new List<XpsTextExtractor.TextBlock>();
                    foreach (var block in pdfBlocks)
                    {
                        var idx = block.Text.IndexOf(searchText, StringComparison.OrdinalIgnoreCase);
                        if (idx >= 0)
                        {
                            var charWidth = block.Width / Math.Max(block.Text.Length, 1);
                            var offsetX = idx * charWidth;
                            var matchWidth = searchText.Length * charWidth;
                            results.Add(new XpsTextExtractor.TextBlock(
                                Text: block.Text,
                                X: (block.X + offsetX) * scale,
                                Y: block.Y * scale,
                                Width: matchWidth * scale,
                                FontSize: block.FontSize * scale,
                                Page: block.Page
                            ));
                        }
                    }
                    if (results.Count == 0)
                    {
                        var lineGroups = pdfBlocks.GroupBy(b => (b.Page, YLine: Math.Round(b.Y / 5) * 5));
                        foreach (var group in lineGroups)
                        {
                            var lineBlocks = group.OrderBy(b => b.X).ToList();
                            var lineText = string.Join(" ", lineBlocks.Select(b => b.Text));
                            var idx2 = lineText.IndexOf(searchText, StringComparison.OrdinalIgnoreCase);
                            if (idx2 >= 0 && lineBlocks.Count > 0)
                            {
                                var first = lineBlocks[0];
                                results.Add(new XpsTextExtractor.TextBlock(
                                    Text: searchText,
                                    X: first.X * scale,
                                    Y: first.Y * scale,
                                    Width: searchText.Length * first.FontSize * 0.5 * scale,
                                    FontSize: first.FontSize * scale,
                                    Page: first.Page
                                ));
                            }
                        }
                    }
                }

                if (page >= 0)
                    results = results.Where(r => r.Page == page).ToList();

                if (results.Count == 0)
                    return ToolResult.Error(
                        $"Текст \"{searchText}\" не найден. Убедитесь, что ищете текст ТОЧНО как написан в документе.");

                if (occurrenceIndex >= results.Count)
                    occurrenceIndex = 0;

                match = results[occurrenceIndex];
                if (fmt == "markup")
                    sizes = PageGeometry.Load(tempPath, ext, new[] { match.Page });
            }
            finally { TryDelete(tempPath); }

            var matchPage = match.Page;

            var snapshotVersion = doc.ActualFileSnapshot.Created.ToUniversalTime();
            var remarkId = Guid.NewGuid();

            var x1 = match.X;
            var yBaseline = match.Y + match.FontSize;
            var x2 = match.X + match.Width;
            var posY = yBaseline + 10;

            var remarkType = _connection.Metadata.Types.FirstOrDefault(t =>
                t.Name.Equals("doc_issue", StringComparison.OrdinalIgnoreCase));
            if (remarkType == null)
                return ToolResult.Error("Тип 'doc_issue' не найден.");

            if (fmt == "markup")
            {
                if (sizes == null || !sizes.TryGetValue(matchPage, out var pageSize))
                    return ToolResult.Error($"Не удалось определить размер страницы {matchPage}.");

                Markup decoration;
                try
                {
                    // Верх строки — match.Y, низ — с запасом на подстрочные элементы (шрифт * 1.2).
                    decoration = MarkupBuilder.TextDecoration(1, matchPage, match.X, match.Y, match.Width,
                        match.FontSize * 1.2, style, color, pageSize.Height);
                }
                catch (ArgumentException ex)
                {
                    return ToolResult.Error(ex.Message);
                }

                var markupRemarkId = SaveMarkupRemark(doc, remarkType.Id, new[] { decoration }, comment);
                return ToolResult.Ok(
                    $"Замечание к тексту создано (Markup, {style}). ID: {markupRemarkId}\n" +
                    $"Текст: \"{searchText}\" (стр. {matchPage}, x={match.X:F0}..{match.X + match.Width:F0})\n" +
                    $"Комментарий: {comment}");
            }

            var container = new AnnotationContainer
            {
                AnnotationId = remarkId,
                Version = snapshotVersion,
                PositionX = x1,
                PositionY = posY,
                PageNumber = matchPage,
                Kind = "TextStickyNote"
            };
            container.WriteData(new TextStickyNoteData
            {
                FixedTextRange = $"{F(x1)},{F(yBaseline)},{F(x2)},{F(yBaseline)}"
            });

            var annotationModifier = new AnnotationModifier(
                _connection.Backend, ChangesetDataSource.Native, MergeChangePolicy.Allow);
            var builder = annotationModifier.CreateAnnotation(remarkId, doc, container, remarkType.Id, snapshotVersion);
            builder.SetAttribute("text", new DValue { StrValue = comment });
            _connection.ApplyAndWait(() => annotationModifier.Apply(null));

            return ToolResult.Ok(
                $"Замечание к тексту создано. ID: {remarkId}\n" +
                $"Текст: \"{searchText}\" (стр. {matchPage}, x={match.X:F0}..{match.X + match.Width:F0})\n" +
                $"Комментарий: {comment}");
        });
    }

    /// <summary>
    /// Пытается извлечь ошибочное слово из комментария.
    /// </summary>
    private static string? TryExtractErrorWord(string content)
    {
        var lower = content.ToLowerInvariant();
        if (!lower.Contains("ошибк") && !lower.Contains("опечатк") &&
            !lower.Contains("должно быть") && !lower.Contains("орфограф"))
            return null;

        var match = System.Text.RegularExpressions.Regex.Match(content, @"[\u00AB\u201E\u201C""](\S+?)[\u00BB\u201D\u201F""]");
        if (match.Success)
            return match.Groups[1].Value;

        var quoteChars = new[] { '\u00AB', '\u00BB', '\u201E', '\u201C', '\u201D', '"' };
        var arrowMatch = System.Text.RegularExpressions.Regex.Match(content, @"(\S+)\s*(?:->|\u2192)\s*\S+");
        if (arrowMatch.Success)
            return arrowMatch.Groups[1].Value.Trim(quoteChars);

        return null;
    }

    [McpServerTool, Description(
        "Нарисовать графическую фигуру на документе (прямоугольник, эллипс, треугольник, стрелку, линию, ломаную, многоугольник, рисунок от руки, точку). " +
        "По умолчанию создаёт замечание в формате Markup (клиент Pilot 26.20+); format=legacy — старый формат для серверов и клиентов старше 26.20 " +
        "(только rectangle, ellipse, triangle, arrow, point, без strokeWidth/points/cloud/startEnding/endEnding). " +
        "Несколько фигур в одном замечании — CreateMarkupAnnotation. " +
        "Для замечаний к тексту с выделением слова лучше подходит AnnotateText. " +
        "КООРДИНАТЫ: передавайте ПИКСЕЛЬНЫЕ координаты прямо с изображения от GetDocumentPageImage. " +
        "Инструмент сам пересчитает их в единицы документа по DPI. " +
        "Rectangle: x,y = левый верхний угол, width/height = размеры. " +
        "Ellipse: x,y = центр объекта, width/height = диаметры. " +
        "Arrow/line: x,y = куда указывает остриё, width/height = смещение хвоста от цели. " +
        "Polyline/polygon/ink: вершины в points. " +
        "Point: x,y = положение стикера.")]
    public Task<string> CreateAnnotation(
        [Description("GUID документа, на котором создаётся замечание")] string documentId,
        [Description("Тип: rectangle, ellipse, triangle, arrow, line, polyline, polygon, ink, point. Для текстовых замечаний используйте AnnotateText!")] string shape,
        [Description("Цвет: red, green, blue, yellow, pink, black или hex #RRGGBB")] string color,
        [Description("Координата X в пикселях изображения. Rectangle/triangle: левый верхний угол. Ellipse: центр. Arrow: цель остриём.")] double x,
        [Description("Координата Y в пикселях изображения.")] double y,
        [Description("Ширина в пикселях. Arrow: смещение хвоста по X. Ellipse: полный диаметр.")] double width = 100,
        [Description("Высота в пикселях. Arrow: смещение хвоста по Y. Ellipse: полный диаметр.")] double height = 60,
        [Description("Номер страницы (начинается с 0)")] int page = 0,
        [Description("Текст пояснения к замечанию. Заполняйте для рецензий и комментариев.")] string content = "",
        [Description("DPI изображения от GetDocumentPageImage (по умолчанию 150)")] int dpi = 150,
        [Description("Системное имя типа замечания в базе")] string remarkTypeName = "doc_issue",
        [Description("Системное имя атрибута для XML-разметки")] string attributeName = "annotation",
        [Description("Толщина линии в DIP (только markup)")] double strokeWidth = 2,
        [Description("Вершины для polyline/polygon/ink в пикселях изображения: 'x1,y1;x2,y2;...'")] string points = "",
        [Description("Облачная граница для polyline/polygon (только markup)")] bool cloud = false,
        [Description("Окончание в начале линии для arrow/line: none, openarrow")] string startEnding = "",
        [Description("Окончание в конце линии (у острия) для arrow/line. У arrow по умолчанию openarrow")] string endEnding = "",
        [Description("Формат замечания: markup (клиент Pilot 26.20+) или legacy (старый, до 26.20)")] string format = "markup")
    {
        // Исходные пиксельные значения — для формата Markup (он сам пересчитывает по dpi)
        var spec = new ShapeSpec
        {
            Shape = shape, X = x, Y = y, Width = width, Height = height, Page = page, Color = color,
            StrokeWidth = strokeWidth, Points = points, Cloud = cloud, StartEnding = startEnding, EndEnding = endEnding
        };

        // Пересчёт пикселей → единицы документа (96 DPI)
        var scale = dpi / 96.0;
        x /= scale;
        y /= scale;
        width /= scale;
        height /= scale;

        return ToolRunner.RunWriteAsync(_connection, "CreateAnnotation",
            $"doc={documentId}, shape={shape}, color={color}, x={x:F1}, y={y:F1}, w={width:F1}, h={height:F1}, page={page}, dpi={dpi}, format={format}", async () =>
        {
            // Перенаправляем текстовые замечания на AnnotateText
            var shapeLower = shape.Trim().ToLowerInvariant();
            if (shapeLower is "text" or "textstickynote" or "textnote")
                return ToolResult.Error(
                    "Для замечаний к тексту используйте AnnotateText(documentId, searchText, comment). " +
                    "CreateAnnotation — только для графических фигур.");

            // Автоматическое перенаправление: если content содержит исправление
            if (!string.IsNullOrEmpty(content))
            {
                var searchWord = TryExtractErrorWord(content);
                if (searchWord != null)
                {
                    Console.Error.WriteLine($"[PilotMCP] CreateAnnotation -> AnnotateText redirect: '{searchWord}'");
                    return await AnnotateText(documentId, searchWord, content, page, color: color, format: format);
                }
            }

            var fmt = format.Trim().ToLowerInvariant();
            if (fmt == "markup")
                return await CreateMarkupRemark(documentId, new List<ShapeSpec> { spec }, content, dpi, remarkTypeName);
            if (fmt != "legacy")
                return ToolResult.Error($"Неизвестный format: {format}. Используйте: markup, legacy");

            var markupOnly = new List<string>();
            if (shapeLower is "polyline" or "polygon" or "ink") markupOnly.Add($"shape={shape}");
            if (strokeWidth != 2) markupOnly.Add("strokeWidth");
            if (points != "") markupOnly.Add("points");
            if (cloud) markupOnly.Add("cloud");
            if (startEnding != "") markupOnly.Add("startEnding");
            if (endEnding != "") markupOnly.Add("endEnding");
            if (markupOnly.Count > 0)
                return ToolResult.Error(
                    $"Параметры {string.Join(", ", markupOnly)} доступны только при format=markup (клиент Pilot 26.20+).");

            if (!Guid.TryParse(documentId, out var docGuid))
                return ToolResult.Error($"Некорректный GUID документа: {documentId}");

            var remarkType = _connection.Metadata.Types.FirstOrDefault(t =>
                t.Name.Equals(remarkTypeName, StringComparison.OrdinalIgnoreCase));
            if (remarkType == null)
                return ToolResult.Error(
                    $"Тип '{remarkTypeName}' не найден в базе. " +
                    "Используйте GetTypes чтобы найти правильное имя типа замечаний.");

            var docObjects = await _connection.ServerApi.GetObjectsAsync(new[] { docGuid });
            if (docObjects == null || docObjects.Count == 0)
                return ToolResult.Error($"Документ {documentId} не найден.");

            var doc = docObjects[0];
            var snapshotVersion = doc.ActualFileSnapshot.Created.ToUniversalTime();

            var remarkId = Guid.NewGuid();

            var container = new AnnotationContainer
            {
                AnnotationId = remarkId,
                Version = snapshotVersion,
                PositionX = x,
                PositionY = y,
                PageNumber = page,
            };

            var hex = AnnotationBuilder.ResolveColor(color);
            switch (shapeLower)
            {
                case "rectangle" or "rect":
                    container.Kind = "RedPencil";
                    container.WriteData(new PencilData
                    {
                        Geometry = $"M{F(x)},{F(y)}L{F(x + width)},{F(y)} {F(x + width)},{F(y + height)} {F(x)},{F(y + height)} {F(x)},{F(y)}z",
                        Color = hex,
                        IsStraightLine = false,
                        GeometryKind = GeometryKind.Rectangle,
                        IsTextNoteVisible = true
                    });
                    break;
                case "ellipse" or "circle":
                    // (x, y) = центр эллипса/круга, width/height = полные размеры
                    container.PositionX = x - width / 2;
                    container.PositionY = y - height / 2;
                    container.Kind = "RedPencil";
                    container.WriteData(new PencilData
                    {
                        Geometry = AnnotationBuilder.GenerateEllipseGeometryString(x, y, width / 2, height / 2),
                        Color = hex,
                        IsStraightLine = false,
                        GeometryKind = GeometryKind.Ellipse,
                        IsTextNoteVisible = true
                    });
                    break;
                case "triangle":
                    container.Kind = "RedPencil";
                    container.WriteData(new PencilData
                    {
                        Geometry = $"M{F(x)},{F(y + height)}L{F(x + width / 2)},{F(y)} {F(x + width)},{F(y + height)} {F(x)},{F(y + height)}z",
                        Color = hex,
                        IsStraightLine = false,
                        GeometryKind = GeometryKind.Triangle,
                        IsTextNoteVisible = true
                    });
                    break;
                case "arrow" or "line":
                    // (x, y) = цель (остриё стрелки)
                    // (x+width, y+height) = хвост (откуда идёт стрелка)
                    var tailX = x + width;
                    var tailY = y + height;
                    container.Kind = "RedPencil";
                    container.WriteData(new PencilData
                    {
                        Geometry = AnnotationBuilder.GenerateArrowGeometryString(tailX, tailY, x, y),
                        Color = hex,
                        IsStraightLine = true,
                        GeometryKind = GeometryKind.Line,
                        IsTextNoteVisible = true
                    });
                    container.PositionX = Math.Min(x, tailX);
                    container.PositionY = Math.Min(y, tailY);
                    break;
                case "point" or "textnote":
                    container.Kind = "TextNote";
                    break;
                default:
                    return ToolResult.Error($"Неизвестная фигура: {shape}. Используйте: rectangle, ellipse, triangle, arrow, point");
            }

            var annotationModifier = new AnnotationModifier(
                _connection.Backend, ChangesetDataSource.Native, MergeChangePolicy.Allow);
            var builder = annotationModifier.CreateAnnotation(remarkId, doc, container, remarkType.Id, snapshotVersion);
            if (!string.IsNullOrWhiteSpace(content))
                builder.SetAttribute("text", new DValue { StrValue = content });
            _connection.ApplyAndWait(() => annotationModifier.Apply(null));

            return ToolResult.Ok(
                $"Замечание создано. ID: {remarkId}\n" +
                $"Фигура: {shape}, цвет: {AnnotationBuilder.ResolveColor(color)}, страница: {page}\n" +
                $"Позиция: ({x}, {y}), размер: {width}x{height}");
        });
    }

    // --- формат Markup (клиент Pilot 26.20+) ---

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) { Console.Error.WriteLine($"[PilotMCP] Не удалось удалить временный файл {path}: {ex.Message}"); }
    }

    /// <summary>
    /// Размеры страниц документа в DIP. Файл берётся так же, как в GetDocumentPageImage (XPS, иначе PDF),
    /// чтобы пиксельные координаты с изображения страницы совпадали. null — нет XPS/PDF.
    /// </summary>
    private Dictionary<int, (double Width, double Height)>? LoadPageSizes(DObject doc, IEnumerable<int> pages)
    {
        foreach (var ext in new[] { ".xps", ".pdf" })
        {
            var file = doc.ActualFileSnapshot.Files.FirstOrDefault(f => f.Name.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
            if (file == null) continue;
            var tempPath = DownloadFile(file.Body.Id, ext);
            try { return PageGeometry.Load(tempPath, ext, pages); }
            finally { TryDelete(tempPath); }
        }
        return null;
    }

    /// <summary>Создаёт замечание с атрибутами markups/markups_version (и text, если задан). Возвращает ID замечания.</summary>
    private Guid SaveMarkupRemark(DObject doc, int remarkTypeId, IEnumerable<Markup> markups, string text)
    {
        var remarkId = Guid.NewGuid();
        var annotationModifier = new AnnotationModifier(
            _connection.Backend, ChangesetDataSource.Native, MergeChangePolicy.Allow);
        var builder = annotationModifier.CreateAnnotation(
            remarkId, doc, markups, remarkTypeId, doc.ActualFileSnapshot.Created.ToUniversalTime());
        if (!string.IsNullOrWhiteSpace(text))
            builder.SetAttribute("text", new DValue { StrValue = text });
        _connection.ApplyAndWait(() => annotationModifier.Apply(null));
        return remarkId;
    }

    private async Task<string> CreateMarkupRemark(
        string documentId, List<ShapeSpec> shapes, string content, int dpi, string remarkTypeName)
    {
        if (dpi <= 0)
            return ToolResult.Error("dpi должен быть больше 0.");
        if (!Guid.TryParse(documentId, out var docGuid))
            return ToolResult.Error($"Некорректный GUID документа: {documentId}");

        var remarkType = _connection.Metadata.Types.FirstOrDefault(t =>
            t.Name.Equals(remarkTypeName, StringComparison.OrdinalIgnoreCase));
        if (remarkType == null)
            return ToolResult.Error(
                $"Тип '{remarkTypeName}' не найден в базе. " +
                "Используйте GetTypes чтобы найти правильное имя типа замечаний.");

        var docObjects = await _connection.ServerApi.GetObjectsAsync(new[] { docGuid });
        if (docObjects == null || docObjects.Count == 0)
            return ToolResult.Error($"Документ {documentId} не найден.");
        var doc = docObjects[0];

        var sizes = LoadPageSizes(doc, shapes.Select(s => s.Page));
        if (sizes == null)
            return ToolResult.Error("Нет XPS или PDF файла.");

        var markups = new List<Markup>();
        try
        {
            for (var i = 0; i < shapes.Count; i++)
            {
                if (!sizes.TryGetValue(shapes[i].Page, out var size))
                    return ToolResult.Error($"Фигура {i + 1}: страницы {shapes[i].Page} нет в документе.");
                markups.Add(MarkupBuilder.Build(i + 1, shapes[i], dpi, size.Height));
            }
        }
        catch (ArgumentException ex)
        {
            return ToolResult.Error(ex.Message);
        }

        var remarkId = SaveMarkupRemark(doc, remarkType.Id, markups, content);
        return ToolResult.Ok(
            $"Замечание создано (Markup). ID: {remarkId}\n" +
            $"Фигур: {markups.Count} ({string.Join(", ", markups.Select(m => m.ToCompact().Subtype))})");
    }

    private static readonly JsonSerializerOptions ShapesJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };

    [McpServerTool, Description(
        "Создать ОДНО замечание из нескольких фигур в формате Markup (клиент Pilot 26.20+). " +
        "shapesJson — JSON-массив фигур с полями как у CreateAnnotation: shape (rectangle, ellipse, triangle, arrow, line, polyline, polygon, ink, point), " +
        "x, y, width, height, page, color, strokeWidth, points, cloud, startEnding, endEnding. " +
        "Координаты — в ПИКСЕЛЯХ изображения от GetDocumentPageImage (dpi должен совпадать). " +
        "Пример: [{\"shape\":\"rectangle\",\"x\":150,\"y\":150,\"width\":200,\"height\":100,\"color\":\"blue\"},{\"shape\":\"arrow\",\"x\":150,\"y\":150,\"width\":80,\"height\":60}]")]
    public Task<string> CreateMarkupAnnotation(
        [Description("GUID документа")] string documentId,
        [Description("JSON-массив фигур")] string shapesJson,
        [Description("Текст замечания")] string content = "",
        [Description("DPI изображения от GetDocumentPageImage (по умолчанию 150)")] int dpi = 150,
        [Description("Системное имя типа замечания в базе")] string remarkTypeName = "doc_issue")
    {
        return ToolRunner.RunWriteAsync(_connection, "CreateMarkupAnnotation",
            $"doc={documentId}, dpi={dpi}, shapes={shapesJson}", async () =>
        {
            List<ShapeSpec?>? shapes;
            try
            {
                shapes = JsonSerializer.Deserialize<List<ShapeSpec?>>(shapesJson, ShapesJsonOptions);
            }
            catch (JsonException ex)
            {
                return ToolResult.Error($"Некорректный shapesJson: {ex.Message}");
            }
            if (shapes == null || shapes.Count == 0)
                return ToolResult.Error("shapesJson должен быть непустым JSON-массивом фигур.");
            if (shapes.Any(s => s == null))
                return ToolResult.Error("shapesJson содержит null вместо фигуры.");

            return await CreateMarkupRemark(documentId, shapes.Select(s => s!).ToList(), content, dpi, remarkTypeName);
        });
    }

    private static bool HasLegacyAnnotation(DObject r)
        => r.Attributes.TryGetValue("annotation", out var a) && !string.IsNullOrEmpty(a.StrValue);

    private static bool InRecycleBin(DObject obj)
        => obj.StateInfo?.State is ObjectState.InRecycleBin or ObjectState.DeletedPermanently;

    [McpServerTool, Description(
        "Прочитать замечания документа (любого типа замечаний, без удалённых в корзину). " +
        "Для каждого: ID, тип, текст, автор, дата создания, версия файла (папка замечаний), формат (markup/legacy) и фигуры: " +
        "вид, страница (с 0), ограничивающий прямоугольник x, y, width, height в ПИКСЕЛЯХ изображения (начало — левый верхний угол) и стиль. " +
        "Для markup читается версия, совпадающая с текущей версией файла, иначе последняя. Результат — JSON-массив.")]
    public Task<string> GetAnnotations(
        [Description("GUID документа")] string documentId,
        [Description("DPI изображения от GetDocumentPageImage, в пикселях которого возвращаются координаты (по умолчанию 150)")] int dpi = 150)
    {
        return ToolRunner.RunAsync(_connection, "GetAnnotations", $"doc={documentId}, dpi={dpi}", async () =>
        {
            if (dpi <= 0)
                return ToolResult.Error("dpi должен быть больше 0.");
            if (!Guid.TryParse(documentId, out var docGuid))
                return ToolResult.Error($"Некорректный GUID документа: {documentId}");

            var docObjects = await _connection.ServerApi.GetObjectsAsync(new[] { docGuid });
            if (docObjects == null || docObjects.Count == 0)
                return ToolResult.Error($"Документ {documentId} не найден.");
            var doc = docObjects[0];

            var folderType = _connection.Metadata.Types.FirstOrDefault(t => t.Name == "doc_remarksFolder");
            if (folderType == null)
                return ToolResult.Error("Тип 'doc_remarksFolder' не найден в базе.");

            var folderIds = doc.Children.Where(c => c.TypeId == folderType.Id).Select(c => c.ObjectId).ToArray();
            var folders = folderIds.Length == 0
                ? new Dictionary<Guid, DObject>()
                : (await _connection.ServerApi.GetObjectsAsync(folderIds))
                    .Where(f => !InRecycleBin(f)).ToDictionary(f => f.Id);

            var remarkIds = folders.Values.SelectMany(f => f.Children.Select(c => c.ObjectId)).ToArray();
            var remarks = remarkIds.Length == 0
                ? new List<DObject>()
                : (await _connection.ServerApi.GetObjectsAsync(remarkIds)).Where(r => !InRecycleBin(r)).ToList();

            // Markup-версия: совпадающая с текущим файлом, иначе последняя
            var snapshotTicks = doc.ActualFileSnapshot.Created.ToUniversalTime().Ticks;
            var markupsByRemark = new Dictionary<Guid, (MarkupVersion Version, bool IsCurrent)>();
            var readErrors = new Dictionary<Guid, string>();
            foreach (var r in remarks)
            {
                if (HasLegacyAnnotation(r)
                    || !r.Attributes.TryGetValue("markups", out var raw) || raw.ArrayByteValue is not { Length: > 0 })
                    continue;
                try
                {
                    var collection = MarkupSerializer.Deserialize(raw.ArrayByteValue);
                    var current = collection.GetVersion(snapshotTicks);
                    var version = current ?? collection.GetLatestVersion();
                    if (version != null)
                        markupsByRemark[r.Id] = (version, current != null);
                }
                catch (Exception ex)
                {
                    readErrors[r.Id] = ex.Message;
                }
            }

            var pages = markupsByRemark.Values.SelectMany(v => v.Version.Markups).Select(m => m.Page - 1).Distinct().ToList();
            var sizes = pages.Count == 0 ? new Dictionary<int, (double Width, double Height)>() : LoadPageSizes(doc, pages);
            if (sizes == null)
                return ToolResult.Error("Нет XPS или PDF файла для пересчёта координат замечаний.");

            var people = await _connection.ServerApi.LoadPeopleAsync();
            var personMap = people.ToDictionary(p => p.Id, p => p.DisplayName ?? p.Login ?? $"#{p.Id}");
            var typeNames = _connection.Metadata.Types.ToDictionary(t => t.Id, t => t.Name);
            var pxPerDip = dpi / 96.0;
            const double DipPerPt = 96.0 / 72.0;

            var result = new List<Dictionary<string, object?>>();
            foreach (var r in remarks.OrderBy(r => r.Created))
            {
                var folder = folders.GetValueOrDefault(r.ParentId);
                var entry = new Dictionary<string, object?>
                {
                    ["id"] = r.Id.ToString(),
                    ["type"] = typeNames.GetValueOrDefault(r.TypeId, $"#{r.TypeId}"),
                    ["text"] = r.Attributes.TryGetValue("text", out var t) ? t.StrValue : null,
                    ["creator"] = personMap.GetValueOrDefault(r.CreatorId, $"#{r.CreatorId}"),
                    ["created"] = r.Created.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                    ["annotationVersion"] = folder != null && folder.Attributes.TryGetValue("annotation_version", out var av)
                        ? av.DateValue?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : null
                };
                result.Add(entry);

                // Одно повреждённое замечание не должно ломать весь список: ошибка выводится в поле error.
                try
                {
                    if (readErrors.TryGetValue(r.Id, out var readError))
                        throw new InvalidOperationException(readError);

                    var shapes = new List<Dictionary<string, object?>>();
                    string format;
                    if (HasLegacyAnnotation(r))
                    {
                        format = "legacy";
                        using var reader = new StringReader(r.Attributes["annotation"].StrValue!);
                        foreach (var c in (List<AnnotationContainer>)AnnotationSerializers.AnnotationSerializer.Deserialize(reader)!)
                            shapes.Add(new()
                            {
                                ["kind"] = c.Kind,
                                ["page"] = c.PageNumber,
                                ["x"] = Math.Round(c.PositionX * pxPerDip, 1),
                                ["y"] = Math.Round(c.PositionY * pxPerDip, 1)
                            });
                    }
                    else if (markupsByRemark.TryGetValue(r.Id, out var mv))
                    {
                        format = "markup";
                        foreach (var m in mv.Version.Markups)
                        {
                            if (!sizes.TryGetValue(m.Page - 1, out var size))
                                throw new InvalidOperationException($"страницы {m.Page - 1} нет в документе.");
                            // единицы Markup (пункты, ось вверх) → пиксели (ось вниз)
                            var pts = MarkupBuilder.Outline(m)
                                .Select(p => (X: p.X * DipPerPt * pxPerDip, Y: (size.Height - p.Y * DipPerPt) * pxPerDip)).ToList();
                            shapes.Add(new()
                            {
                                ["subtype"] = m.ToCompact().Subtype.ToString(),
                                ["page"] = m.Page - 1,
                                ["x"] = pts.Count == 0 ? null : Math.Round(pts.Min(p => p.X), 1),
                                ["y"] = pts.Count == 0 ? null : Math.Round(pts.Min(p => p.Y), 1),
                                ["width"] = pts.Count == 0 ? null : Math.Round(pts.Max(p => p.X) - pts.Min(p => p.X), 1),
                                ["height"] = pts.Count == 0 ? null : Math.Round(pts.Max(p => p.Y) - pts.Min(p => p.Y), 1),
                                ["color"] = m.Style.ColorRgb is { } c ? MarkupBuilder.FormatColor(c) : null,
                                ["strokeWidth"] = m.Style.StrokeWidth
                            });
                        }
                    }
                    else
                    {
                        format = "none"; // замечание без геометрии (только текст)
                    }

                    entry["format"] = format;
                    entry["markupVersionIsCurrent"] = format == "markup" ? markupsByRemark[r.Id].IsCurrent : null;
                    entry["shapes"] = shapes;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[PilotMCP] GetAnnotations: замечание {r.Id}: {ex.Message}");
                    entry["error"] = ex.Message;
                }
            }

            var options = new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
            return ToolResult.Ok(JsonSerializer.Serialize(result, options));
        });
    }

    private static string F(double value) => value.ToString("F6", System.Globalization.CultureInfo.InvariantCulture);

    [McpServerTool, Description(
        "Получить XML-разметку аннотации в старом (legacy) формате без создания объекта. " +
        "Для замечаний в формате Markup (клиент Pilot 26.20+) не подходит. " +
        "Полезно для проверки или ручной вставки в атрибут.")]
    public string BuildAnnotationXml(
        [Description("Тип фигуры: rectangle, ellipse, triangle, arrow, point")] string shape,
        [Description("Цвет: red, green, blue, yellow, pink, black или hex #RRGGBB")] string color,
        [Description("Координата X")] double x,
        [Description("Координата Y")] double y,
        [Description("Ширина (для arrow: смещение по X)")] double width = 100,
        [Description("Высота (для arrow: смещение по Y)")] double height = 60,
        [Description("Номер страницы")] int page = 0)
    {
        try
        {
            var xml = shape.ToLowerInvariant() switch
            {
                "rectangle" or "rect" => AnnotationBuilder.Rectangle(x, y, width, height, page, color),
                "ellipse" or "circle" => AnnotationBuilder.Ellipse(x, y, width / 2, height / 2, page, color), // x,y = center
                "triangle" => AnnotationBuilder.Triangle(x, y, width, height, page, color),
                "arrow" or "line" => AnnotationBuilder.Arrow(x + width, y + height, x, y, page, color),
                "point" or "textnote" => AnnotationBuilder.TextNote(x, y, page),
                "textstickynote" or "stickynote" or "text" => AnnotationBuilder.TextStickyNote(x, y, width, height, page),
                _ => throw new ArgumentException($"Неизвестная фигура: {shape}")
            };
            return ToolResult.Ok($"XML-разметка:\n{xml}");
        }
        catch (ArgumentException ex)
        {
            return ToolResult.Error(ex.Message);
        }
    }
}
