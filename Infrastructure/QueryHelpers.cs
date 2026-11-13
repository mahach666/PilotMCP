using System.Globalization;
using System.Text;

namespace PilotMCP;

/// <summary>Чистые хелперы разбора аргументов поиска и файловых инструментов (без зависимости от сервера).</summary>
public static class QueryHelpers
{
    private const string LuceneSpecials = "+-&|!(){}[]^\"~*?:\\/";

    /// <summary>Экранирует спецсимволы Lucene в одном слове, чтобы оно искалось буквально.</summary>
    public static string EscapeLucene(string word)
    {
        // AND/OR/NOT в верхнем регистре — операторы Lucene (запрос с ними сервер не исполняет);
        // анализатор всё равно приводит слова к нижнему регистру, поэтому так ищется буквально.
        if (word is "AND" or "OR" or "NOT") return word.ToLowerInvariant();
        var sb = new StringBuilder(word.Length + 4);
        foreach (var c in word)
        {
            if (LuceneSpecials.Contains(c)) sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Фраза в кавычках: многословное значение остаётся одним термом поля.</summary>
    public static string QuoteLucene(string value)
        => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    /// <summary>Разбивает текст на слова и экранирует каждое. Пустой результат — пустой массив.</summary>
    public static string[] EscapedWords(string text)
        => (text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(EscapeLucene).ToArray();

    /// <summary>
    /// Разбор даты (InvariantCulture, без времени = UTC). Для верхней границы без времени
    /// берётся конец этого дня (иначе объекты, созданные в этот день, выпадают из диапазона).
    /// </summary>
    public static bool TryParseDate(string s, bool upperBound, out DateTime utc)
    {
        utc = default;
        s = s.Trim();
        if (!DateTime.TryParse(s, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out utc))
            return false;
        if (upperBound && !s.Contains(':'))
            utc = utc.AddDays(1).AddSeconds(-1);
        return true;
    }

    /// <summary>
    /// Разбор пары границ дат. Пустая строка = нет границы. Возвращает текст ошибки или null.
    /// </summary>
    public static string? ParseRange(string from, string to, string fromName, string toName,
        out DateTime dateFrom, out DateTime dateTo)
    {
        dateFrom = DateTime.MinValue;
        dateTo = DateTime.MaxValue;
        if (!string.IsNullOrWhiteSpace(from) && !TryParseDate(from, false, out dateFrom))
            return $"Некорректная дата {fromName}: '{from}'. Ожидается ISO 8601, например 2025-01-31.";
        if (!string.IsNullOrWhiteSpace(to) && !TryParseDate(to, true, out dateTo))
            return $"Некорректная дата {toName}: '{to}'. Ожидается ISO 8601, например 2025-01-31.";
        if (dateFrom > dateTo)
            return $"Дата {fromName} позже даты {toName}.";
        return null;
    }

    /// <summary>Поле CSV: кавычки при разделителе, кавычке, переводе строки или возврате каретки.</summary>
    public static string CsvEscape(string s, string separator)
        => s.Contains(separator) || s.Contains('"') || s.Contains('\n') || s.Contains('\r')
            ? $"\"{s.Replace("\"", "\"\"")}\""
            : s;

    /// <summary>
    /// Проверяет пару входной/выходной файл: совпадение путей (запись обнулила бы вход до чтения)
    /// и создаёт каталог вывода. Возвращает текст ошибки или null.
    /// </summary>
    public static string? PrepareOutput(string inputFile, string outputFile)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var full = Path.GetFullPath(outputFile);
        if (string.Equals(Path.GetFullPath(inputFile), full, comparison))
            return "outputFile совпадает с inputFile: запись затёрла бы входной файл. Укажите другой путь или оставьте outputFile пустым.";
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        return null;
    }
}
