using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text;

namespace PilotMCP;

[McpServerToolType]
public class DocumentTools
{
    private readonly PilotConnection _connection;
    public DocumentTools(PilotConnection connection) => _connection = connection;

    private static readonly string TempDir = Path.Combine(Path.GetTempPath(), "pilot-mcp");
    private const int MinDpi = 36, MaxDpi = 300, MaxImageSidePx = 2000;

    static DocumentTools() => SweepOldTempFiles();

    /// <summary>Удаляет оставшиеся от упавших вызовов копии документов и PNG страниц старше суток.</summary>
    private static void SweepOldTempFiles()
    {
        try
        {
            if (!Directory.Exists(TempDir)) return;
            var cutoff = DateTime.UtcNow - TimeSpan.FromHours(24);
            foreach (var file in Directory.EnumerateFiles(TempDir, "doc_*").Concat(Directory.EnumerateFiles(TempDir, "page_*.png")))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    ToolLogger.Log("DocumentTools", "sweep", $"Не удалось удалить {file}: {ex.Message}");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ToolLogger.Log("DocumentTools", "sweep", $"Очистка временных файлов не удалась: {ex.Message}");
        }
    }

    private static void DeleteTemp(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ToolLogger.Log("DocumentTools", "delete", $"Не удалось удалить {path}: {ex.Message}");
        }
    }

    private static int PageCount(string path, bool isPdf)
    {
        if (!isPdf) return XpsTextExtractor.GetPageCount(path);
        using var pdf = UglyToad.PdfPig.PdfDocument.Open(path);
        return pdf.NumberOfPages;
    }

    private const string UnitsText = "XPS — 1/96 дюйма, PDF — пункты (1/72 дюйма); y — верх текста сверху вниз. ";

    /// <summary>
    /// Скачивает файл документа во временную папку. Возвращает (путь, расширение).
    /// Приоритет: XPS > PDF.
    /// </summary>
    private (string path, string ext)? DownloadDocumentFile(Ascon.Pilot.DataClasses.DObject doc, string[]? preferredExts = null)
    {
        var exts = preferredExts ?? new[] { ".xps", ".pdf" };
        Ascon.Pilot.DataClasses.DFile? file = null;
        string ext = "";
        foreach (var e in exts)
        {
            file = doc.ActualFileSnapshot.Files
                .FirstOrDefault(f => f.Name.EndsWith(e, StringComparison.OrdinalIgnoreCase));
            if (file != null) { ext = e; break; }
        }
        if (file == null) return null;

        var tempPath = Helpers.DownloadToTemp(_connection.FileApi, file.Body.Id, ext);
        return (tempPath, ext);
    }

    [McpServerTool, Description(
        "Найти текст на страницах документа и получить координаты всех вхождений (x, y, ширина, размер шрифта). " +
        "Единицы координат зависят от формата: " + UnitsText +
        "Для создания замечаний к тексту используйте AnnotateText вместо этого инструмента.")]
    public Task<string> FindTextOnPage(
        [Description("GUID документа в Pilot")] string documentId,
        [Description("Текст для поиска")] string searchText)
    {
        return ToolRunner.RunAsync(_connection, "FindTextOnPage", $"doc={documentId}, text={searchText}", async () =>
        {
            if (!Guid.TryParse(documentId, out var guid))
                return ToolResult.Error($"Некорректный GUID: {documentId}");
            if (string.IsNullOrEmpty(searchText))
                return ToolResult.Error("Текст для поиска не может быть пустым.");

            var objects = await _connection.ServerApi.GetObjectsAsync(new[] { guid });
            if (objects == null || objects.Count == 0)
                return ToolResult.Error($"Документ {documentId} не найден.");

            var doc = objects[0];
            var downloaded = DownloadDocumentFile(doc);
            if (downloaded == null)
                return ToolResult.Error(
                    "XPS или PDF файл не найден. Файлы: " +
                    string.Join(", ", doc.ActualFileSnapshot.Files.Select(f => f.Name)));

            var (tempPath, ext) = downloaded.Value;
            try
            {
                var isPdf = ext == ".pdf";
                var results = isPdf
                    ? PdfTextExtractor.FindText(tempPath, searchText)
                    : XpsTextExtractor.FindText(tempPath, searchText);
                return XpsTextExtractor.FormatSearchResults(results, searchText,
                    isPdf ? "пункты (1/72 дюйма, PDF)" : "1/96 дюйма (XPS)");
            }
            finally
            {
                DeleteTemp(tempPath);
            }
        });
    }

    [McpServerTool, Description(
        "Прочитать текст документа (PDF или XPS). Для больших документов загружайте постранично (page=0, page=1, ...). " +
        "Сначала вызовите без параметра page, чтобы узнать количество страниц, затем читайте по одной. " +
        "Номер страницы вне диапазона — ошибка; если в документе нет текстового слоя (скан), об этом сообщается. " +
        "Используйте для анализа содержимого (проверка орфографии, поиск ошибок и т.д.). " +
        "После анализа вызывайте AnnotateText для каждой найденной ошибки — он работает с любыми документами.")]
    public Task<string> GetDocumentText(
        [Description("GUID документа в Pilot")] string documentId,
        [Description("Номер страницы (с 0). Если не указан — возвращает все страницы (для коротких документов) или только сводку с количеством страниц (для длинных).")] int page = -1,
        [Description("Макс. символов в ответе (0 = без лимита, по умолчанию 10000)")] int maxChars = 10000)
    {
        return ToolRunner.RunAsync(_connection, "GetDocumentText", $"doc={documentId}, page={page}", async () =>
        {
            if (!Guid.TryParse(documentId, out var guid))
                return ToolResult.Error($"Некорректный GUID: {documentId}");

            var objects = await _connection.ServerApi.GetObjectsAsync(new[] { guid });
            if (objects == null || objects.Count == 0)
                return ToolResult.Error($"Документ {documentId} не найден.");

            var doc = objects[0];
            var downloaded = DownloadDocumentFile(doc);
            if (downloaded == null)
                return ToolResult.Error(
                    "Нет XPS или PDF файла. Файлы: " +
                    string.Join(", ", doc.ActualFileSnapshot.Files.Select(f => f.Name)));

            var (tempPath, ext) = downloaded.Value;
            bool isPdf = ext == ".pdf";

            List<XpsTextExtractor.TextBlock> allBlocks;
            int totalPages;
            try
            {
                totalPages = PageCount(tempPath, isPdf);
                allBlocks = isPdf
                    ? PdfTextExtractor.ExtractTextBlocks(tempPath)
                    : XpsTextExtractor.ExtractText(tempPath);
            }
            finally
            {
                DeleteTemp(tempPath);
            }

            if (page >= totalPages)
                return ToolResult.Error($"Страница {page} вне диапазона: в документе {totalPages} стр. (нумерация с 0).");

            var filtered = page >= 0
                ? allBlocks.Where(b => b.Page == page).ToList()
                : allBlocks;

            if (allBlocks.Count == 0)
                return ToolResult.Ok($"В документе {totalPages} стр., но текстового слоя нет (возможно, скан). Используйте GetDocumentPageImage.");

            if (page < 0 && totalPages > 3)
            {
                var preview = new StringBuilder();
                preview.AppendLine($"Документ содержит {totalPages} страниц.");
                preview.AppendLine("Загружайте постранично: GetDocumentText(documentId, page=0), page=1, ...");
                preview.AppendLine();
                var firstPageText = FormatPageText(allBlocks.Where(b => b.Page == 0), isPdf);
                preview.AppendLine("=== Страница 0 (превью) ===");
                preview.Append(maxChars > 0 ? Helpers.Truncate(firstPageText, maxChars) : firstPageText);

                return ToolResult.Ok(preview.ToString());
            }

            var sb = new StringBuilder();
            var pageGroups = filtered.GroupBy(b => b.Page).OrderBy(g => g.Key);
            foreach (var pg in pageGroups)
            {
                sb.AppendLine($"=== Страница {pg.Key} ===");
                sb.Append(FormatPageText(pg, isPdf));
                sb.AppendLine();
            }

            var info = page >= 0
                ? $"Страница {page} из {totalPages}"
                : $"Все {totalPages} страниц";

            var text = sb.ToString();
            if (maxChars > 0 && text.Length > maxChars)
                text = Helpers.Truncate(text, maxChars);

            return ToolResult.Ok($"{info}:\n\n{text}");
        });
    }

    /// <summary>
    /// Склеивает текстовые блоки в читаемые строки.
    /// </summary>
    private static string FormatPageText(IEnumerable<XpsTextExtractor.TextBlock> blocks, bool isPdf)
    {
        var sb = new StringBuilder();
        var sorted = blocks.OrderBy(b => b.Y).ThenBy(b => b.X).ToList();
        if (sorted.Count == 0) return "";

        var lines = sorted.Aggregate(new List<List<XpsTextExtractor.TextBlock>>(), (acc, block) =>
        {
            if (acc.Count == 0 || Math.Abs(acc.Last().First().Y - block.Y) > block.FontSize * 0.5)
                acc.Add(new List<XpsTextExtractor.TextBlock>());
            acc.Last().Add(block);
            return acc;
        });

        var separator = isPdf ? " " : "";
        foreach (var line in lines)
        {
            var lineText = string.Join(separator, line.OrderBy(b => b.X).Select(b => b.Text));
            if (!string.IsNullOrWhiteSpace(lineText))
                sb.AppendLine(lineText.Trim());
        }
        return sb.ToString();
    }

    [McpServerTool, Description(
        "Извлечь весь текст со страницы документа с координатами. " +
        "Работает с XPS и PDF документами. Результат записывается в файл (JSONL). " +
        "Единицы координат (x, y, width, fontSize) зависят от формата: " + UnitsText)]
    public Task<string> ExtractPageText(
        [Description("GUID документа в Pilot")] string documentId,
        [Description("Номер страницы (начинается с 0). Если -1, извлечь все страницы.")] int page = -1)
    {
        return ToolRunner.RunAsync(_connection, "ExtractPageText", $"doc={documentId}, page={page}", async () =>
        {
            if (!Guid.TryParse(documentId, out var guid))
                return ToolResult.Error($"Некорректный GUID: {documentId}");

            var objects = await _connection.ServerApi.GetObjectsAsync(new[] { guid });
            if (objects == null || objects.Count == 0)
                return ToolResult.Error($"Документ {documentId} не найден.");

            var doc = objects[0];
            var downloaded = DownloadDocumentFile(doc);
            if (downloaded == null)
                return ToolResult.Error(
                    "XPS или PDF файл не найден. Файлы: " +
                    string.Join(", ", doc.ActualFileSnapshot.Files.Select(f => f.Name)));

            var (tempPath, ext) = downloaded.Value;
            List<XpsTextExtractor.TextBlock> allBlocks;
            try
            {
                allBlocks = ext == ".pdf"
                    ? PdfTextExtractor.ExtractTextBlocks(tempPath)
                    : XpsTextExtractor.ExtractText(tempPath);
            }
            finally
            {
                DeleteTemp(tempPath);
            }

            var filtered = page >= 0 ? allBlocks.Where(b => b.Page == page).ToList() : allBlocks;

            string[] columns = ["text", "x", "y", "width", "fontSize", "page"];
            var (path, count) = TempFiles.WriteJsonl(filtered, b => new Dictionary<string, object>
            {
                ["text"] = b.Text,
                ["x"] = Math.Round(b.X, 1),
                ["y"] = Math.Round(b.Y, 1),
                ["width"] = Math.Round(b.Width, 1),
                ["fontSize"] = Math.Round(b.FontSize, 1),
                ["page"] = b.Page
            }, "pagetext");

            var unit = ext == ".pdf" ? "пункты (1/72 дюйма, PDF)" : "1/96 дюйма (XPS)";
            return ToolResult.File(path, count, columns,
                $"Извлечено {count} текстовых блоков" + (page >= 0 ? $" со страницы {page}" : "") +
                $". Единицы x, y, width, fontSize: {unit}; y — верх текста сверху вниз.");
        });
    }

    [McpServerTool, Description(
        "Получить изображение страницы документа (PDF или XPS). " +
        "Возвращает PNG-картинку страницы для визуального анализа. " +
        "dpi от 36 до 300; если длинная сторона получается больше 2000 px (крупные форматы), dpi снижается автоматически — " +
        "фактический dpi указан в ответе, его нужно передавать в CreateAnnotation. " +
        "Номер страницы вне диапазона — ошибка.")]
    public async Task<IEnumerable<Content>> GetDocumentPageImage(
        [Description("GUID документа в Pilot")] string documentId,
        [Description("Номер страницы (с 0)")] int page = 0,
        [Description("DPI для рендеринга, 36-300 (по умолчанию 150, для экономии контекста)")] int dpi = 150)
    {
        var logArgs = $"doc={documentId}, page={page}, dpi={dpi}";
        try
        {
            _connection.EnsureConnected();
            if (!Guid.TryParse(documentId, out var guid))
                return new[] { new Content { Type = "text", Text = $"Некорректный GUID: {documentId}" } };
            if (dpi < MinDpi || dpi > MaxDpi)
                return new[] { new Content { Type = "text", Text = $"dpi должен быть от {MinDpi} до {MaxDpi}, получено {dpi}." } };
            if (page < 0)
                return new[] { new Content { Type = "text", Text = $"Номер страницы не может быть отрицательным: {page}." } };

            var objects = await _connection.ServerApi.GetObjectsAsync(new[] { guid });
            if (objects == null || objects.Count == 0)
                return new[] { new Content { Type = "text", Text = $"Документ {documentId} не найден." } };

            var doc = objects[0];
            var downloaded = DownloadDocumentFile(doc);
            if (downloaded == null)
                return new[] { new Content { Type = "text", Text = "Нет PDF или XPS файла." } };

            var (tempPath, ext) = downloaded.Value;
            byte[] pngBytes;
            int effectiveDpi;

            try
            {
                pngBytes = ext == ".pdf"
                    ? RenderPdfPage(tempPath, page, dpi, out effectiveDpi)
                    : RenderXpsPage(tempPath, page, dpi, out effectiveDpi);
            }
            finally
            {
                DeleteTemp(tempPath);
            }

            Directory.CreateDirectory(TempDir);
            var savedPath = Path.Combine(TempDir, $"page_{documentId[..8]}_{page}.png");
            File.WriteAllBytes(savedPath, pngBytes);

            var base64 = Convert.ToBase64String(pngBytes);
            ToolLogger.Log("GetDocumentPageImage", logArgs, $"OK, {pngBytes.Length} bytes PNG -> {savedPath}");

            // Размер изображения в пикселях
            int imgWidth = 0, imgHeight = 0;
            using (var imgStream = new MemoryStream(pngBytes))
            {
                // Читаем размер PNG из заголовка (IHDR: offset 16-23)
                if (pngBytes.Length > 24)
                {
                    imgWidth = (pngBytes[16] << 24) | (pngBytes[17] << 16) | (pngBytes[18] << 8) | pngBytes[19];
                    imgHeight = (pngBytes[20] << 24) | (pngBytes[21] << 16) | (pngBytes[22] << 8) | pngBytes[23];
                }
            }

            var coordInfo = $"\n\nДЛЯ АННОТАЦИЙ (CreateAnnotation):\n" +
                $"- Размер изображения: {imgWidth} x {imgHeight} пикселей\n" +
                $"- DPI: {dpi}\n" +
                $"- Передавайте координаты в ПИКСЕЛЯХ этого изображения, инструмент сам пересчитает.\n" +
                $"- Параметр dpi={effectiveDpi} в CreateAnnotation.";

            return new[]
            {
                new Content { Type = "text", Text = $"Страница {page} документа ({pngBytes.Length / 1024} KB, {effectiveDpi} DPI{(effectiveDpi < dpi ? $", снижен с {dpi}: длинная сторона не больше {MaxImageSidePx} px" : "")}). Файл: {savedPath}{coordInfo}" },
                new Content { Type = "image", Data = base64, MimeType = "image/png" }
            };
        }
        catch (Exception ex)
        {
            ToolLogger.LogError("GetDocumentPageImage", logArgs, ex);
            return new[] { new Content { Type = "text", Text = $"Ошибка: {ex.Message}" } };
        }
    }

    /// <summary>dpi, при котором длинная сторона страницы (в пунктах) не превышает MaxImageSidePx.</summary>
    private static int FitDpi(int dpi, double widthPt, double heightPt)
    {
        var maxPx = Math.Max(widthPt, heightPt) * dpi / 72.0;
        return maxPx <= MaxImageSidePx ? dpi : Math.Max(1, (int)Math.Floor(dpi * MaxImageSidePx / maxPx));
    }

    private static byte[] RenderPdfPage(string pdfPath, int page, int dpi, out int effectiveDpi)
    {
        using (var pdf = UglyToad.PdfPig.PdfDocument.Open(pdfPath))
        {
            if (page >= pdf.NumberOfPages)
                throw new ArgumentException($"Страница {page} вне диапазона: в документе {pdf.NumberOfPages} стр. (нумерация с 0).");
            var pg = pdf.GetPage(page + 1);
            effectiveDpi = FitDpi(dpi, pg.Width, pg.Height);
        }

        var pdfBytes = File.ReadAllBytes(pdfPath);
        using var ms = new MemoryStream();
        PDFtoImage.Conversion.SavePng(ms, pdfBytes, page, options: new PDFtoImage.RenderOptions { Dpi = effectiveDpi });
        return ms.ToArray();
    }

    private static byte[] RenderXpsPage(string xpsPath, int page, int dpi, out int effectiveDpi)
    {
        var tempPng = Path.Combine(Path.GetTempPath(), $"xps_render_{Guid.NewGuid()}.png");
        try
        {
            using var ctx = new MuPDFCore.MuPDFContext();
            using var document = new MuPDFCore.MuPDFDocument(ctx, xpsPath);

            if (page >= document.Pages.Count)
                throw new ArgumentException($"Страница {page} вне диапазона: в документе {document.Pages.Count} стр. (нумерация с 0).");
            var rect = document.Pages[page].Bounds; // пункты (1/72 дюйма)
            effectiveDpi = FitDpi(dpi, rect.Width, rect.Height);

            document.SaveImage(page, effectiveDpi / 72.0, MuPDFCore.PixelFormats.RGB, tempPng, MuPDFCore.RasterOutputFileTypes.PNG);
            return File.ReadAllBytes(tempPng);
        }
        finally
        {
            DeleteTemp(tempPng);
        }
    }

}
