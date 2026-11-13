using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using SkiaSharp;

namespace PilotMCP;

/// <summary>
/// Извлекает текст с координатами из XPS-документов.
/// XPS — это ZIP-архив с XAML-страницами, содержащими элементы Glyphs.
/// </summary>
public static class XpsTextExtractor
{
    private static readonly XNamespace XpsNs = "http://schemas.microsoft.com/xps/2005/06";
    private static readonly XNamespace OpenXpsNs = "http://schemas.openxps.org/oxps/v1.0";

    public record TextBlock(string Text, double X, double Y, double Width, double FontSize, int Page, double[]? Advances = null, double[]? NaturalAdvances = null);

    /// <summary>
    /// Извлекает все текстовые блоки из XPS-файла.
    /// Поддерживает как обычные .fpage файлы, так и interleaved (piece) формат.
    /// </summary>
    public static List<TextBlock> ExtractText(string xpsPath)
    {
        var result = new List<TextBlock>();

        using var zip = ZipFile.OpenRead(xpsPath);

        var pageXmls = LoadFixedPages(zip);
        using var fonts = new FontCache(zip);

        for (int pageIdx = 0; pageIdx < pageXmls.Count; pageIdx++)
        {
            var (pagePath, doc) = pageXmls[pageIdx];
            if (doc.Root == null) continue;

            var ns = doc.Root.Name.Namespace;
            ExtractGlyphsFromElement(doc.Root, ns, pageIdx, result, pagePath, fonts);
        }

        return result;
    }

    /// <summary>
    /// Размеры страниц (ширина, высота) в DIP (1/96") из атрибутов FixedPage — по индексу страницы с 0.
    /// </summary>
    public static List<(double Width, double Height)> GetPageSizes(string xpsPath)
    {
        using var zip = ZipFile.OpenRead(xpsPath);
        return LoadFixedPages(zip).Select((p, i) =>
        {
            var d = p.Doc;
            var w = ParseDouble(d.Root?.Attribute("Width")?.Value);
            var h = ParseDouble(d.Root?.Attribute("Height")?.Value);
            if (w <= 0 || h <= 0)
                throw new InvalidDataException($"Страница {i}: у FixedPage нет корректных Width/Height.");
            return (w, h);
        }).ToList();
    }

    // Числа в имени сравниваем как числа: "10.fpage" идёт после "2.fpage", а не перед ним.
    private static string NumericKey(string name)
        => Regex.Replace(name, @"\d+", m => m.Value.PadLeft(10, '0'));

    /// <summary>
    /// Загружает FixedPage XML из XPS-архива в порядке документа: _rels/.rels → FixedDocumentSequence →
    /// FixedDocument → PageContent (так же нумерует страницы рендерер MuPDF в GetDocumentPageImage).
    /// Части могут лежать целиком (Pages/1.fpage) или кусками (Pages/1.fpage/[0].piece, [1].last.piece).
    /// </summary>
    private static List<(string Path, XDocument Doc)> LoadFixedPages(ZipArchive zip)
    {
        var budget = new ReadBudget(MaxTotalBytes);
        var order = PageOrder(zip, budget);
        if (order != null)
            return order.Select(p => (p, XDocument.Parse(ReadPart(zip, p, budget)
                ?? throw new InvalidDataException($"В XPS нет страницы '{p}', указанной в FixedDocument.")))).ToList();

        // shortcut: пакет без FixedDocumentSequence (нестандартный) — порядок по номерам в именах файлов
        var directPages = zip.Entries
            .Where(e => e.FullName.EndsWith(".fpage", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => NumericKey(e.FullName))
            .ToList();

        if (directPages.Count > 0)
        {
            if (directPages.Count > MaxPages)
                throw new InvalidDataException($"В XPS больше {MaxPages} страниц.");
            var docs = new List<(string, XDocument)>();
            foreach (var entry in directPages)
                docs.Add((entry.FullName, XDocument.Parse(DecodeText(ReadEntries(new[] { entry }, entry.FullName, MaxPartBytes, budget)))));
            return docs;
        }

        // Interleaved (piece) формат: группируем куски по имени fpage
        var pieceGroups = zip.Entries
            .Where(e => e.FullName.Contains(".fpage/") &&
                        e.FullName.EndsWith(".piece", StringComparison.OrdinalIgnoreCase))
            .GroupBy(e =>
            {
                var idx = e.FullName.IndexOf(".fpage/", StringComparison.OrdinalIgnoreCase);
                return e.FullName[..(idx + 6)]; // "Documents/1/Pages/1.fpage"
            })
            .OrderBy(g => NumericKey(g.Key))
            .ToList();

        if (pieceGroups.Count > MaxPages)
            throw new InvalidDataException($"В XPS больше {MaxPages} страниц.");
        var result = new List<(string, XDocument)>();
        foreach (var group in pieceGroups)
        {
            var xml = DecodeText(ReadEntries(group.OrderBy(e => NumericKey(e.FullName)), group.Key, MaxPartBytes, budget));
            if (!string.IsNullOrWhiteSpace(xml))
                result.Add((group.Key, XDocument.Parse(xml)));
        }

        return result;
    }

    /// <summary>Пути страниц в порядке FixedDocumentSequence; null, если в пакете нет последовательности.</summary>
    private static List<string>? PageOrder(ZipArchive zip, ReadBudget budget)
    {
        var rels = ReadPart(zip, "_rels/.rels", budget);
        var seqTarget = rels == null ? null : XDocument.Parse(rels).Root?.Elements()
            .FirstOrDefault(r => ((string?)r.Attribute("Type"))?.EndsWith("/fixedrepresentation", StringComparison.OrdinalIgnoreCase) == true)
            ?.Attribute("Target")?.Value;
        if (seqTarget == null) return null;
        var seqPath = ResolvePart("", seqTarget);
        var seq = ReadPart(zip, seqPath, budget);
        if (seq == null) return null;

        var pages = new List<string>();
        foreach (var docRef in XDocument.Parse(seq).Root!.Elements().Where(e => e.Name.LocalName == "DocumentReference"))
        {
            var docPath = ResolvePart(seqPath, (string)docRef.Attribute("Source")!);
            var doc = ReadPart(zip, docPath, budget)
                ?? throw new InvalidDataException($"В XPS нет FixedDocument '{docPath}'.");
            pages.AddRange(XDocument.Parse(doc).Root!.Elements().Where(e => e.Name.LocalName == "PageContent")
                .Select(pc => ResolvePart(docPath, (string)pc.Attribute("Source")!)));
            // повторные ссылки на одну страницу легальны (MuPDF покажет обе), поэтому ограничиваем число, а не дубли
            if (pages.Count > MaxPages)
                throw new InvalidDataException($"В XPS больше {MaxPages} страниц.");
        }
        return pages;
    }

    // XPS приходит из базы Pilot (загрузить его может любой пользователь) — ограничиваем распаковку против zip-бомб.
    private const long MaxPartBytes = 64L << 20;   // одна часть (страница, FixedDocument и т.п.)
    private const long MaxTotalBytes = 512L << 20; // все XML-части одного извлечения
    private const long MaxFontBytes = 32L << 20;   // один шрифт
    private const long MaxFontsTotalBytes = 128L << 20;
    private const int MaxPages = 10_000;

    private sealed class ReadBudget(long bytes) { public long Remaining = bytes; }

    /// <summary>Распаковывает записи подряд, не больше maxBytes на часть и не больше остатка бюджета.</summary>
    private static byte[] ReadEntries(IEnumerable<ZipArchiveEntry> entries, string name, long maxBytes, ReadBudget budget)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        foreach (var entry in entries)
        {
            using var s = entry.Open();
            int n;
            while ((n = s.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (ms.Length + n > maxBytes || n > budget.Remaining)
                    throw new InvalidDataException($"Часть XPS '{name}' после распаковки превышает допустимый размер.");
                budget.Remaining -= n;
                ms.Write(buffer, 0, n);
            }
        }
        return ms.ToArray();
    }

    /// <summary>Текст части с определением кодировки по BOM (XPS допускает UTF-8 и UTF-16).</summary>
    private static string DecodeText(byte[] bytes)
    {
        using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    /// <summary>Ссылка Source (абсолютная или относительно части basePart) → имя записи в ZIP.</summary>
    private static string ResolvePart(string basePart, string source)
        => Uri.UnescapeDataString(new Uri(new Uri("http://xps/" + basePart), source).AbsolutePath).TrimStart('/');

    /// <summary>Записи ZIP части пакета: одна целая или упорядоченные куски [N].piece; пусто, если части нет.</summary>
    private static ZipArchiveEntry[] PartEntries(ZipArchive zip, string name)
    {
        var direct = zip.Entries.FirstOrDefault(e => e.FullName.Equals(name, StringComparison.OrdinalIgnoreCase));
        return direct != null
            ? new[] { direct }
            : zip.Entries
                .Where(e => e.FullName.StartsWith(name + "/", StringComparison.OrdinalIgnoreCase)
                            && e.FullName.EndsWith(".piece", StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => int.Parse(Regex.Match(e.Name, @"^\[(\d+)\]").Groups[1].Value))
                .ToArray();
    }

    /// <summary>Содержимое части пакета: целиком или склеенное из кусков [N].piece; null, если части нет.</summary>
    private static string? ReadPart(ZipArchive zip, string name, ReadBudget budget)
    {
        var parts = PartEntries(zip, name);
        // куски склеиваются по байтам: граница [N].piece может разрезать многобайтовый символ
        return parts.Length == 0 ? null : DecodeText(ReadEntries(parts, name, MaxPartBytes, budget));
    }

    /// <summary>Число страниц XPS (по FixedDocument, как нумерует рендерер).</summary>
    public static int GetPageCount(string xpsPath)
    {
        using var zip = ZipFile.OpenRead(xpsPath);
        return LoadFixedPages(zip).Count;
    }

    /// <summary>Смещение по X границы n-го символа внутри блока (по Advances, иначе поровну).</summary>
    private static double OffsetIn(TextBlock block, int n)
    {
        if (block.Advances != null && block.Advances.Length >= block.Text.Length)
            return block.Advances.Take(n).Sum();
        return n * block.Width / Math.Max(block.Text.Length, 1);
    }

    /// <summary>
    /// Смещение правой границы символа n-1 (конец совпадения, n символов от начала блока). Advance последнего
    /// глифа в Indices может включать зазор до следующей колонки: берём не больше натурального из шрифта.
    /// </summary>
    private static double EndOffsetIn(TextBlock block, int n)
    {
        if (n < 1 || block.Advances == null || block.Advances.Length < block.Text.Length)
            return OffsetIn(block, n);
        var last = block.Advances[n - 1];
        var natural = block.NaturalAdvances is { } na && na.Length >= n ? na[n - 1] : 0;
        if (natural > 0 && natural < last) last = natural;
        return OffsetIn(block, n - 1) + last;
    }

    /// <summary>
    /// Ищет текст в XPS-файле и возвращает координаты всех вхождений.
    /// Единицы координат — 1/96 дюйма (FixedPage), Y — верх текста сверху вниз.
    /// </summary>
    public static List<TextBlock> FindText(string xpsPath, string searchText)
    {
        if (string.IsNullOrEmpty(searchText))
            throw new ArgumentException("Текст для поиска не может быть пустым.", nameof(searchText));

        var allBlocks = ExtractText(xpsPath);
        var found = new List<TextBlock>();

        // Точные вхождения внутри блока (все, не только первое) — смещение по Advances
        foreach (var block in allBlocks)
        {
            for (var start = 0; start <= block.Text.Length - searchText.Length;)
            {
                var idx = block.Text.IndexOf(searchText, start, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) break;
                var offsetX = OffsetIn(block, idx);
                found.Add(block with
                {
                    X = block.X + offsetX,
                    Width = EndOffsetIn(block, idx + searchText.Length) - offsetX
                });
                start = idx + searchText.Length;
            }
        }

        // Если в отдельных блоках не найдено — склеиваем соседние блоки одной строки
        if (found.Count == 0)
        {
            found = FindInConcatenatedBlocks(allBlocks, searchText);
        }

        return found;
    }

    private static void ExtractGlyphsFromElement(XElement element, XNamespace ns, int pageIdx, List<TextBlock> result, string pagePath, FontCache fonts)
    {
        // Process Glyphs elements
        foreach (var glyph in element.Descendants(ns + "Glyphs"))
        {
            var text = glyph.Attribute("UnicodeString")?.Value;
            // Префикс "{}" — escape-последовательность XPS, в текст не входит
            if (text != null && text.StartsWith("{}", StringComparison.Ordinal)) text = text[2..];
            if (string.IsNullOrEmpty(text)) continue;

            var originX = ParseDouble(glyph.Attribute("OriginX")?.Value);
            var originY = ParseDouble(glyph.Attribute("OriginY")?.Value);
            var fontSize = ParseDouble(glyph.Attribute("FontRenderingEmSize")?.Value);

            // Compose full RenderTransform chain from Glyph to FixedPage root (inside→out)
            var chain = new List<double[]>();

            // Collect transforms from glyph → root (inside out order)
            var cur = glyph;
            while (cur != null && cur != element)
            {
                var rt = RenderTransformOf(cur);
                if (!string.IsNullOrEmpty(rt))
                    chain.Add(ParseMatrix(rt));
                cur = cur.Parent;
            }

            // Apply composed transform to the origin point
            var (px, py) = ApplyTransformChain(chain, originX, originY);

            // Compute effective scale for font size and width estimation
            var scaleX = ComputeScaleX(chain);
            var effectiveFontSize = fontSize * scaleX;

            // Parse Indices for precise per-character advance widths
            var indicesStr = glyph.Attribute("Indices")?.Value;
            var advances = ParseAdvances(indicesStr, text.Length, fontSize, scaleX);
            var totalWidth = advances.Sum();
            var fontUri = glyph.Attribute("FontUri")?.Value;
            var typeface = string.IsNullOrEmpty(fontUri) ? null : fonts.Get(ResolvePart(pagePath, fontUri));
            var natural = typeface == null ? null : NaturalAdvances(typeface, indicesStr, text, fontSize, scaleX);

            result.Add(new TextBlock(
                Text: text,
                X: px,
                Y: py - effectiveFontSize, // Adjust from baseline to top of text
                Width: totalWidth,
                FontSize: effectiveFontSize,
                Page: pageIdx,
                Advances: advances,
                NaturalAdvances: natural
            ));
        }
    }

    /// <summary>RenderTransform элемента: атрибут или свойство-элемент Element.RenderTransform/MatrixTransform@Matrix.</summary>
    private static string? RenderTransformOf(XElement el)
    {
        var attr = el.Attribute("RenderTransform")?.Value;
        if (!string.IsNullOrEmpty(attr)) return attr;
        return el.Elements().FirstOrDefault(c => c.Name.LocalName == el.Name.LocalName + ".RenderTransform")
            ?.Elements().FirstOrDefault(m => m.Name.LocalName == "MatrixTransform")
            ?.Attribute("Matrix")?.Value;
    }

    /// <summary>
    /// Parses a RenderTransform string "a,b,c,d,e,f" into a matrix array [a,b,c,d,e,f].
    /// Matrix transforms point (x,y) to: x' = a*x + c*y + e, y' = b*x + d*y + f
    /// </summary>
    private static double[] ParseMatrix(string rt)
    {
        var parts = rt.Split(',');
        if (parts.Length == 6)
        {
            return new[]
            {
                ParseDouble(parts[0]), ParseDouble(parts[1]),
                ParseDouble(parts[2]), ParseDouble(parts[3]),
                ParseDouble(parts[4]), ParseDouble(parts[5])
            };
        }
        return new double[] { 1, 0, 0, 1, 0, 0 }; // identity
    }

    /// <summary>
    /// Applies a chain of transform matrices to a point.
    /// </summary>
    private static (double x, double y) ApplyTransformChain(List<double[]> chain, double x, double y)
    {
        foreach (var m in chain)
        {
            var nx = m[0] * x + m[2] * y + m[4];
            var ny = m[1] * x + m[3] * y + m[5];
            x = nx;
            y = ny;
        }
        return (x, y);
    }

    /// <summary>
    /// Computes the effective horizontal scale from a chain of transforms.
    /// </summary>
    private static double ComputeScaleX(List<double[]> chain)
    {
        // Transform unit vector (1,0) and measure its length
        var (x1, y1) = ApplyTransformChain(chain, 1, 0);
        var (x0, y0) = ApplyTransformChain(chain, 0, 0);
        var dx = x1 - x0;
        var dy = y1 - y0;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>
    /// Parses the Indices attribute to extract per-character advance widths in FixedPage coordinates.
    /// XPS Indices format: entries separated by ';': "[(codeUnits:glyphs)]glyph,advance[,uOff,vOff]".
    /// Advance is in hundredths of FontRenderingEmSize. Кластер "(n:m)" — n символов на m глифов
    /// (m записей подряд): суммарная ширина делится поровну между его n символами.
    /// </summary>
    private static double[] ParseAdvances(string? indices, int charCount, double fontSize, double scaleX)
    {
        var advances = new double[charCount];
        var defaultAdvance = fontSize * 0.5 * scaleX; // fallback

        if (string.IsNullOrEmpty(indices))
        {
            Array.Fill(advances, defaultAdvance);
            return advances;
        }

        var entries = indices.Split(';');
        int e = 0, i = 0;
        while (i < charCount)
        {
            if (e >= entries.Length) { advances[i++] = defaultAdvance; continue; }

            var (codeUnits, glyphs) = ParseCluster(entries[e]);
            double sum = 0;
            for (int g = 0; g < glyphs && e + g < entries.Length; g++)
                sum += EntryAdvance(entries[e + g], fontSize, scaleX) ?? defaultAdvance;
            e += glyphs;

            var n = Math.Min(codeUnits, charCount - i);
            for (int k = 0; k < n; k++) advances[i + k] = sum / n;
            i += n;
        }

        return advances;
    }

    /// <summary>
    /// Натуральная ширина каждого символа из шрифта (FixedPage-единицы); 0 — неизвестна (кластеры, нет глифа).
    /// Номер глифа берётся из Indices, а если его там нет — из cmap шрифта по символу.
    /// </summary>
    private static double[] NaturalAdvances(SKTypeface typeface, string? indices, string text, double fontSize, double scaleX)
    {
        var result = new double[text.Length];
        var glyphIds = new ushort[text.Length];
        var entries = string.IsNullOrEmpty(indices) ? Array.Empty<string>() : indices.Split(';');
        var fromCmap = typeface.GetGlyphs(text);
        int e = 0, i = 0;
        while (i < text.Length)
        {
            var (codeUnits, glyphs) = e < entries.Length ? ParseCluster(entries[e]) : (1, 1);
            if (codeUnits == 1 && glyphs == 1)
                glyphIds[i] = e < entries.Length && EntryGlyph(entries[e]) is { } g ? g : (i < fromCmap.Length ? fromCmap[i] : (ushort)0);
            e += glyphs;
            i += Math.Min(codeUnits, text.Length - i);
        }
        using var font = new SKFont(typeface, (float)fontSize) { Subpixel = true, LinearMetrics = true, Hinting = SKFontHinting.None };
        var widths = font.GetGlyphWidths(glyphIds);
        for (int k = 0; k < result.Length; k++)
            if (glyphIds[k] != 0) result[k] = widths[k] * scaleX;
        return result;
    }

    /// <summary>Номер глифа из записи "[(n:m)]glyph,advance..."; null — не задан.</summary>
    private static ushort? EntryGlyph(string entry)
    {
        var close = entry.IndexOf(')');
        if (entry.TrimStart().StartsWith('(') && close >= 0) entry = entry[(close + 1)..];
        var comma = entry.IndexOf(',');
        var head = (comma < 0 ? entry : entry[..comma]).Trim();
        return ushort.TryParse(head, NumberStyles.None, CultureInfo.InvariantCulture, out var g) ? g : null;
    }

    /// <summary>Шрифты одного извлечения: грузятся по требованию, кэшируются по части, освобождаются в Dispose.</summary>
    private sealed class FontCache(ZipArchive zip) : IDisposable
    {
        private readonly Dictionary<string, SKTypeface?> _fonts = new(StringComparer.OrdinalIgnoreCase);
        private readonly ReadBudget _fontBudget = new(MaxFontsTotalBytes);

        public SKTypeface? Get(string part)
        {
            if (_fonts.TryGetValue(part, out var cached)) return cached;
            SKTypeface? typeface = null;
            try
            {
                var entries = PartEntries(zip, part);
                if (entries.Length == 0) throw new InvalidDataException("часть пакета не найдена");
                var data = ReadEntries(entries, part, MaxFontBytes, _fontBudget);
                if (part.EndsWith(".odttf", StringComparison.OrdinalIgnoreCase))
                    Deobfuscate(data, Path.GetFileNameWithoutExtension(part));
                using var skData = SKData.CreateCopy(data);
                typeface = SKTypeface.FromData(skData)
                    ?? throw new InvalidDataException("SkiaSharp не распознал шрифт");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"XPS: не удалось загрузить шрифт '{part}': {ex.Message}; ширина последнего глифа берётся из Indices.");
            }
            _fonts[part] = typeface;
            return typeface;
        }

        public void Dispose()
        {
            foreach (var f in _fonts.Values) f?.Dispose();
            _fonts.Clear();
        }
    }

    /// <summary>
    /// Снимает обфускацию .odttf (XPS 2.1.7): ключ — 16 байт GUID из имени файла в порядке записи hex-цифр
    /// (без дефисов); первые 32 байта шрифта XOR ключ в обратном порядке (data[i], data[i+16] ^= key[15-i]).
    /// Операция симметрична: повторный вызов обфусцирует обратно.
    /// </summary>
    internal static void Deobfuscate(byte[] data, string guidName)
    {
        var hex = guidName.Replace("-", "").Trim('{', '}');
        if (hex.Length != 32 || !hex.All(Uri.IsHexDigit))
            throw new InvalidDataException($"имя обфусцированного шрифта '{guidName}' не GUID");
        if (data.Length < 32) throw new InvalidDataException("файл шрифта короче 32 байт");
        var key = Convert.FromHexString(hex);
        for (int i = 0; i < 16; i++)
        {
            data[i] ^= key[15 - i];
            data[i + 16] ^= key[15 - i];
        }
    }

    /// <summary>Префикс кластера "(codeUnits:glyphs)" записи Indices; без префикса — 1:1.</summary>
    private static (int CodeUnits, int Glyphs) ParseCluster(string entry)
    {
        var m = Regex.Match(entry, @"^\s*\((\d+):(\d+)\)");
        if (m.Success
            && int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var cu)
            && int.TryParse(m.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var gl)
            && cu > 0 && gl > 0)
            return (cu, gl);
        return (1, 1);
    }

    /// <summary>Ширина глифа из записи "[(n:m)]glyph,advance[,u,v]" в координатах FixedPage; null — не задана.</summary>
    private static double? EntryAdvance(string entry, double fontSize, double scaleX)
    {
        var close = entry.IndexOf(')');
        if (entry.TrimStart().StartsWith('(') && close >= 0) entry = entry[(close + 1)..];
        var commaIdx = entry.IndexOf(',');
        if (commaIdx < 0) return null;
        var afterComma = entry[(commaIdx + 1)..];
        var nextComma = afterComma.IndexOf(',');
        var advRaw = ParseDouble(nextComma >= 0 ? afterComma[..nextComma] : afterComma);
        // Advance in hundredths of em size, converted to FixedPage units
        return advRaw > 0 ? advRaw / 100.0 * fontSize * scaleX : null;
    }

    private static List<TextBlock> FindInConcatenatedBlocks(List<TextBlock> blocks, string searchText)
    {
        var found = new List<TextBlock>();

        foreach (var line in GroupIntoLines(blocks))
        {
            var lineText = string.Join("", line.Select(b => b.Text));
            // Начало каждого блока в склеенной строке
            var starts = new int[line.Count];
            for (int i = 1; i < line.Count; i++) starts[i] = starts[i - 1] + line[i - 1].Text.Length;

            for (var start = 0; start <= lineText.Length - searchText.Length;)
            {
                var idx = lineText.IndexOf(searchText, start, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) break;
                var end = idx + searchText.Length; // исключительно

                var first = BlockAt(starts, idx);
                var last = BlockAt(starts, end - 1);
                var x0 = line[first].X + OffsetIn(line[first], idx - starts[first]);
                var x1 = line[last].X + EndOffsetIn(line[last], end - starts[last]);

                found.Add(new TextBlock(
                    Text: lineText.Substring(idx, searchText.Length),
                    X: x0,
                    Y: line[first].Y, // Y блока уже верх текста
                    Width: x1 - x0,
                    FontSize: line[first].FontSize,
                    Page: line[first].Page));
                start = end;
            }
        }

        return found;
    }

    /// <summary>Индекс блока, содержащего символ pos склеенной строки (блоки непустые).</summary>
    private static int BlockAt(int[] starts, int pos)
    {
        var i = Array.BinarySearch(starts, pos);
        return i >= 0 ? i : ~i - 1;
    }

    /// <summary>Блоки → строки по базовой линии (Y + FontSize) с допуском 0.5 размера шрифта; внутри строки по X.</summary>
    private static List<List<TextBlock>> GroupIntoLines(List<TextBlock> blocks)
    {
        var lines = new List<List<TextBlock>>();
        var ordered = blocks.OrderBy(b => b.Page).ThenBy(b => b.Y + b.FontSize).ThenBy(b => b.X);
        foreach (var block in ordered)
        {
            var head = lines.Count > 0 ? lines[^1][0] : null;
            var tolerance = Math.Max(block.FontSize * 0.5, 1);
            if (head == null || head.Page != block.Page
                || Math.Abs((head.Y + head.FontSize) - (block.Y + block.FontSize)) > tolerance)
                lines.Add(new List<TextBlock>());
            lines[^1].Add(block);
        }
        return lines.Select(l => l.OrderBy(b => b.X).ToList()).ToList();
    }

    private static double ParseDouble(string? value)
    {
        if (string.IsNullOrEmpty(value)) return 0;
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result);
        return result;
    }

    /// <summary>
    /// Форматирует результат поиска текста для модели.
    /// </summary>
    public static string FormatSearchResults(List<TextBlock> results, string searchText, string? unit = null)
    {
        if (results.Count == 0)
            return $"Текст \"{searchText}\" не найден в документе.";

        var sb = new StringBuilder();
        sb.AppendLine($"Найдено {results.Count} вхождений \"{searchText}\":");
        if (unit != null) sb.AppendLine($"Единицы x, y, ширина, шрифт: {unit}; y — верх текста сверху вниз.");
        sb.AppendLine();
        foreach (var r in results)
        {
            sb.AppendLine($"  Страница {r.Page}: x={r.X:F1}, y={r.Y:F1}, ширина={r.Width:F1}, шрифт={r.FontSize:F1}");
            sb.AppendLine($"    Текст: \"{r.Text}\"");
        }
        return sb.ToString();
    }
}
