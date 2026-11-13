using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace PilotMCP;

/// <summary>
/// Извлекает текст из PDF-документов с помощью PdfPig.
/// Используется как fallback когда XPS-файл недоступен.
/// </summary>
public static class PdfTextExtractor
{
    /// <summary>
    /// Извлекает текст из PDF, возвращая строки по страницам.
    /// </summary>
    public static List<(int Page, string Text)> ExtractText(string pdfPath)
    {
        var result = new List<(int Page, string Text)>();

        using var document = PdfDocument.Open(pdfPath);
        for (int i = 1; i <= document.NumberOfPages; i++)
        {
            var page = document.GetPage(i);
            var words = page.GetWords().ToList();

            if (words.Count == 0)
            {
                result.Add((i - 1, ""));
                continue;
            }

            // Группируем слова по строкам (близкие Y = одна строка)
            // PDF координаты: Y растёт снизу вверх, сортируем по убыванию Y для порядка чтения
            var lines = new List<List<Word>>();
            foreach (var word in words.OrderByDescending(w => w.BoundingBox.BottomLeft.Y).ThenBy(w => w.BoundingBox.BottomLeft.X))
            {
                var wordY = word.BoundingBox.BottomLeft.Y;
                var wordHeight = word.BoundingBox.Height;
                var threshold = Math.Max(wordHeight * 0.5, 2);

                var matchedLine = lines.FirstOrDefault(line =>
                    Math.Abs(line[0].BoundingBox.BottomLeft.Y - wordY) < threshold);

                if (matchedLine != null)
                    matchedLine.Add(word);
                else
                    lines.Add(new List<Word> { word });
            }

            // Собираем текст строк
            var pageText = string.Join("\n",
                lines.Select(line =>
                    string.Join(" ", line.OrderBy(w => w.BoundingBox.BottomLeft.X).Select(w => w.Text))));

            result.Add((i - 1, pageText));
        }

        return result;
    }

    /// <summary>
    /// Ищет текст в PDF-файле и возвращает координаты всех вхождений.
    /// Использует посимвольный поиск через GetLetters() для максимальной надёжности.
    /// Координаты в top-down системе (72 DPI).
    /// </summary>
    public static List<XpsTextExtractor.TextBlock> FindText(string pdfPath, string searchText)
    {
        if (string.IsNullOrEmpty(searchText))
            throw new ArgumentException("Текст для поиска не может быть пустым.", nameof(searchText));

        var found = new List<XpsTextExtractor.TextBlock>();

        using var document = PdfDocument.Open(pdfPath);
        for (int pageNum = 1; pageNum <= document.NumberOfPages; pageNum++)
        {
            var page = document.GetPage(pageNum);
            var letters = page.Letters
                .Where(l => !string.IsNullOrEmpty(l.Value))
                .OrderByDescending(l => l.Location.Y)
                .ThenBy(l => l.Location.X)
                .ToList();

            if (letters.Count == 0) continue;

            // Группируем буквы по строкам (близкие Y = одна строка)
            var lines = GroupLettersIntoLines(letters);

            foreach (var line in lines)
            {
                var sortedLetters = line.OrderBy(l => l.Location.X).ToList();

                // Собираем текст строки и индексы букв
                var lineText = string.Join("", sortedLetters.Select(l => l.Value));
                // Value буквы может быть длиннее одного символа (лигатура "fi", суррогатная пара):
                // индекс символа строки -> индекс буквы
                var charToLetter = new List<int>(lineText.Length);
                for (int li = 0; li < sortedLetters.Count; li++)
                    charToLetter.AddRange(Enumerable.Repeat(li, sortedLetters[li].Value.Length));

                // Ищем все вхождения в строке
                int startIdx = 0;
                while (startIdx <= lineText.Length - searchText.Length)
                {
                    var idx = lineText.IndexOf(searchText, startIdx, StringComparison.OrdinalIgnoreCase);
                    if (idx < 0) break;

                    // Вычисляем координаты из позиций букв
                    var firstLetter = sortedLetters[charToLetter[idx]];
                    var lastLetter = sortedLetters[charToLetter[idx + searchText.Length - 1]];

                    var x = firstLetter.Location.X;
                    var matchWidth = (lastLetter.Location.X + lastLetter.Width) - firstLetter.Location.X;
                    var fontSize = firstLetter.PointSize > 0 ? firstLetter.PointSize : firstLetter.GlyphRectangle.Height;

                    found.Add(new XpsTextExtractor.TextBlock(
                        Text: lineText.Substring(idx, searchText.Length),
                        X: x,
                        Y: page.Height - firstLetter.GlyphRectangle.Top, // top-down
                        Width: matchWidth,
                        FontSize: fontSize,
                        Page: pageNum - 1
                    ));

                    startIdx = idx + searchText.Length;
                }
            }
        }

        // Если посимвольный поиск не дал результатов, пробуем через слова (GetWords)
        // На случай если GetLetters вернул пустой результат для какого-то PDF
        if (found.Count == 0)
        {
            found = FindTextViaWords(pdfPath, searchText);
        }

        return found;
    }

    /// <summary>
    /// Группирует буквы в строки по Y-позиции.
    /// </summary>
    private static List<List<Letter>> GroupLettersIntoLines(List<Letter> letters)
    {
        var lines = new List<List<Letter>>();
        foreach (var letter in letters)
        {
            var letterY = letter.Location.Y;
            var height = letter.GlyphRectangle.Height;
            var threshold = Math.Max(height * 0.5, 2);

            var matchedLine = lines.FirstOrDefault(line =>
                Math.Abs(line[0].Location.Y - letterY) < threshold);

            if (matchedLine != null)
                matchedLine.Add(letter);
            else
                lines.Add(new List<Letter> { letter });
        }
        return lines;
    }

    /// <summary>
    /// Поиск через GetWords() — fallback если GetLetters() не работает.
    /// </summary>
    private static List<XpsTextExtractor.TextBlock> FindTextViaWords(string pdfPath, string searchText)
    {
        var allBlocks = ExtractTextBlocks(pdfPath);
        var found = new List<XpsTextExtractor.TextBlock>();

        // Поиск в отдельных блоках (словах)
        foreach (var block in allBlocks)
        {
            var idx = block.Text.IndexOf(searchText, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                var charWidth = block.Width / Math.Max(block.Text.Length, 1);
                found.Add(block with
                {
                    X = block.X + idx * charWidth,
                    Width = searchText.Length * charWidth
                });
            }
        }

        // Склеенные строки (без пробелов и с пробелами)
        if (found.Count == 0)
        {
            var lineGroups = allBlocks
                .GroupBy(b => (b.Page, YLine: Math.Round(b.Y / 5) * 5))
                .OrderBy(g => g.Key.Page)
                .ThenBy(g => g.Key.YLine);

            foreach (var group in lineGroups)
            {
                var lineBlocks = group.OrderBy(b => b.X).ToList();

                // Пробуем оба варианта склейки
                foreach (var sep in new[] { "", " " })
                {
                    var lineText = string.Join(sep, lineBlocks.Select(b => b.Text));
                    var idx = lineText.IndexOf(searchText, StringComparison.OrdinalIgnoreCase);
                    if (idx >= 0 && lineBlocks.Count > 0)
                    {
                        var first = lineBlocks[0];
                        var charWidth = first.FontSize * 0.5;
                        found.Add(new XpsTextExtractor.TextBlock(
                            Text: searchText,
                            X: first.X + idx * charWidth,
                            Y: first.Y,
                            Width: searchText.Length * charWidth,
                            FontSize: first.FontSize,
                            Page: first.Page
                        ));
                        break;
                    }
                }
            }
        }

        return found;
    }

    /// <summary>
    /// Извлекает текстовые блоки с координатами (для совместимости с XpsTextExtractor.TextBlock).
    /// Координаты в top-down системе (72 DPI).
    /// </summary>
    public static List<XpsTextExtractor.TextBlock> ExtractTextBlocks(string pdfPath)
    {
        var result = new List<XpsTextExtractor.TextBlock>();

        using var document = PdfDocument.Open(pdfPath);
        for (int i = 1; i <= document.NumberOfPages; i++)
        {
            var page = document.GetPage(i);
            foreach (var word in page.GetWords())
            {
                var box = word.BoundingBox;
                result.Add(new XpsTextExtractor.TextBlock(
                    Text: word.Text,
                    X: box.BottomLeft.X,
                    Y: page.Height - box.TopLeft.Y, // Конвертируем в top-down для единообразия
                    Width: box.Width,
                    FontSize: box.Height,
                    Page: i - 1
                ));
            }
        }

        return result;
    }
}
