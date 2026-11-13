using Ascon.Pilot.DataClasses;
using Ascon.Pilot.Server.Api.Contracts;
using System.Globalization;
using System.Text.Json;

namespace PilotMCP;

// Чистые функции без зависимости от PilotConnection — вынесены в отдельный файл, чтобы линковаться в unit-тесты.
internal static partial class Helpers
{
    /// <summary>
    /// Скачивает файл из архива Pilot по чанкам. Возвращает количество скачанных байт.
    /// Пишет во временный savePath + ".part" и переименовывает только после полного успеха,
    /// чтобы сбой не затирал существующий файл и не оставлял обрезок.
    /// </summary>
    public static long DownloadToFile(IFileArchiveApi fileApi, Guid bodyId, string savePath)
    {
        var dir = Path.GetDirectoryName(savePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        const int chunkSize = 1024 * 1024;
        var partPath = savePath + ".part";
        long offset = 0;
        var ok = false;
        try
        {
            using (var fs = File.Create(partPath))
            {
                while (true)
                {
                    var chunk = fileApi.GetFileChunk(bodyId, offset, chunkSize);
                    if (chunk == null || chunk.Length == 0) break;
                    fs.Write(chunk, 0, chunk.Length);
                    offset += chunk.Length;
                    if (chunk.Length < chunkSize) break;
                }
            }

            if (offset == 0)
                throw new InvalidOperationException(
                    $"Сервер вернул пустое содержимое файла (Body.Id={bodyId}). Файл не сохранён.");

            File.Move(partPath, savePath, overwrite: true);
            ok = true;
        }
        finally
        {
            if (!ok) File.Delete(partPath);
        }
        return offset;
    }

    private const int MaxAttrValueLength = 500;

    public static string FormatDValue(DValue? val, int maxLength = MaxAttrValueLength)
    {
        if (val == null) return "";
        if (val.DateValue.HasValue) return val.DateValue.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        if (!string.IsNullOrEmpty(val.StrValue)) return Truncate(val.StrValue!, maxLength);
        if (val.GuidValue is { } g && g != Guid.Empty) return g.ToString();
        if (val.ArrayValue != null && val.ArrayValue.Any())
            return Truncate(string.Join(", ", val.ArrayValue), maxLength);
        if (val.ArrayIntValue != null && val.ArrayIntValue.Any())
            return Truncate(string.Join(", ", val.ArrayIntValue), maxLength);
        if (val.IntValue is { } i) return i.ToString(CultureInfo.InvariantCulture);
        if (val.DoubleValue is { } d) return d.ToString(CultureInfo.InvariantCulture);
        if (val.DecimalValue is { } m) return m.ToString(CultureInfo.InvariantCulture);
        return "";
    }

    public static string Truncate(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength) return text;
        return text[..maxLength] + $"... ({text.Length} chars total)";
    }

    /// <summary>
    /// Разбирает JSON-объект атрибутов. Пустая строка → null (атрибутов нет).
    /// JSON null пропускается; массивы чисел → ArrayIntValue, массивы строк → ArrayValue.
    /// Некорректный JSON или неподдерживаемое значение → ArgumentException (инструмент вернёт ошибку).
    /// </summary>
    public static Dictionary<string, DValue>? ParseAttributes(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            var dict = new Dictionary<string, DValue>();
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("атрибуты должны быть JSON-объектом вида {\"имя\":\"значение\"}");

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                var v = prop.Value;
                switch (v.ValueKind)
                {
                    case JsonValueKind.Null:
                        continue;
                    case JsonValueKind.String:
                        dict[prop.Name] = new DValue { StrValue = v.GetString()! };
                        break;
                    case JsonValueKind.Number when v.TryGetInt64(out var l):
                        dict[prop.Name] = new DValue { IntValue = l };
                        break;
                    case JsonValueKind.Number:
                        dict[prop.Name] = new DValue { DoubleValue = v.GetDouble() };
                        break;
                    case JsonValueKind.True:
                    case JsonValueKind.False:
                        dict[prop.Name] = new DValue { IntValue = v.GetBoolean() ? 1 : 0 };
                        break;
                    case JsonValueKind.Array:
                        dict[prop.Name] = ParseArray(prop.Name, v);
                        break;
                    default:
                        throw new ArgumentException($"атрибут '{prop.Name}': вложенные объекты не поддерживаются");
                }
            }
            return dict;
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"Некорректный JSON атрибутов: {ex.Message}", ex);
        }
    }

    private static DValue ParseArray(string name, JsonElement array)
    {
        var items = array.EnumerateArray().ToList();
        if (items.Count > 0 && items.All(e => e.ValueKind == JsonValueKind.String))
            return new DValue { ArrayValue = items.Select(e => e.GetString()!).ToArray() };
        if (items.Count > 0 && items.All(e => e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out _)))
            return new DValue { ArrayIntValue = items.Select(e => e.GetInt32()).ToArray() };
        throw new ArgumentException(
            $"атрибут '{name}': массив должен быть непустым и состоять только из строк или только из целых чисел");
    }
}
