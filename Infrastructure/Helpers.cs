using Ascon.Pilot.Common;
using Ascon.Pilot.DataClasses;
using Ascon.Pilot.Server.Api.Contracts;
using System.Text.Json;

namespace PilotMCP;

internal static class Helpers
{
    private static readonly string TempDocDir = Path.Combine(Path.GetTempPath(), "pilot-mcp");

    /// <summary>
    /// Скачивает файл из архива Pilot по чанкам. Возвращает количество скачанных байт.
    /// </summary>
    public static long DownloadToFile(IFileArchiveApi fileApi, Guid bodyId, string savePath)
    {
        var dir = Path.GetDirectoryName(savePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        const int chunkSize = 1024 * 1024;
        long offset = 0;
        using var fs = File.Create(savePath);
        while (true)
        {
            var chunk = fileApi.GetFileChunk(bodyId, offset, chunkSize);
            if (chunk == null || chunk.Length == 0) break;
            fs.Write(chunk, 0, chunk.Length);
            offset += chunk.Length;
            if (chunk.Length < chunkSize) break;
        }
        return offset;
    }

    /// <summary>
    /// Скачивает файл во временную папку pilot-mcp. Возвращает путь к файлу.
    /// </summary>
    public static string DownloadToTemp(IFileArchiveApi fileApi, Guid bodyId, string ext)
    {
        var tempPath = Path.Combine(TempDocDir, $"doc_{Guid.NewGuid():N}{ext}");
        DownloadToFile(fileApi, bodyId, tempPath);
        return tempPath;
    }

    /// <summary>
    /// Создать дочерний объект через Modifier SDK.
    /// </summary>
    public static Guid CreateChildObject(PilotConnection connection, Guid parentId, int typeId,
        Dictionary<string, DValue>? attributes = null, Guid? predefinedId = null)
    {
        var newId = predefinedId ?? Guid.NewGuid();
        var modifier = connection.CreateModifier();
        var builder = modifier.CreateObject(newId, parentId, typeId);

        if (attributes != null)
        {
            foreach (var kvp in attributes)
                builder.SetAttribute(kvp.Key, kvp.Value);
        }

        if (modifier.AnyChanges())
            modifier.Apply(null);
        return newId;
    }

    public static Dictionary<string, object> ObjectToDict(DMetadata metadata, DObject obj)
    {
        var dict = new Dictionary<string, object>
        {
            ["id"] = obj.Id.ToString(),
            ["typeId"] = obj.TypeId,
            ["typeName"] = GetTypeName(metadata, obj.TypeId),
            ["parentId"] = obj.ParentId.ToString(),
            ["name"] = GetDisplayName(obj, metadata),
            ["created"] = obj.Created.ToString("yyyy-MM-dd HH:mm:ss"),
            ["childrenCount"] = obj.Children.Count,
            ["filesCount"] = obj.ActualFileSnapshot.Files.Count,
        };

        // Flatten attributes
        var attrs = new Dictionary<string, object>();
        foreach (var attr in obj.Attributes)
            attrs[attr.Key] = FormatDValue(attr.Value);
        dict["attributes"] = attrs;

        return dict;
    }

    public static string GetTypeName(DMetadata metadata, int typeId)
    {
        var type = metadata.Types.FirstOrDefault(t => t.Id == typeId);
        return type?.Name ?? $"unknown({typeId})";
    }

    public static string GetDisplayName(DObject obj, DMetadata? metadata = null)
    {
        // Используем SDK-расширение NObjectExtensions.GetTitle если есть тип
        if (metadata != null)
        {
            var type = metadata.Types.FirstOrDefault(t => t.Id == obj.TypeId);
            if (type != null)
            {
                var title = NObjectExtensions.GetTitle(obj, type);
                if (!string.IsNullOrWhiteSpace(title))
                    return title;
            }
        }

        // Фоллбэк — ищем по известным атрибутам
        foreach (var key in new[] { "name", "title", "project_name", "display_name" })
        {
            if (obj.Attributes.TryGetValue(key, out var val) && !string.IsNullOrWhiteSpace(val.StrValue))
                return val.StrValue;
        }
        foreach (var attr in obj.Attributes)
        {
            if (!string.IsNullOrWhiteSpace(attr.Value.StrValue))
                return attr.Value.StrValue;
        }
        return obj.Id.ToString();
    }

    private const int MaxAttrValueLength = 500;

    public static string FormatDValue(DValue? val, int maxLength = MaxAttrValueLength)
    {
        if (val == null) return "";
        if (val.DateValue.HasValue) return val.DateValue.Value.ToString("yyyy-MM-dd HH:mm:ss");
        if (!string.IsNullOrEmpty(val.StrValue)) return Truncate(val.StrValue!, maxLength);
        if (val.GuidValue != Guid.Empty) return val.GuidValue.ToString()!;
        if (val.ArrayValue != null && val.ArrayValue.Any())
            return Truncate(string.Join(", ", val.ArrayValue), maxLength);
        if (val.ArrayIntValue != null && val.ArrayIntValue.Any())
            return Truncate(string.Join(", ", val.ArrayIntValue), maxLength);
        if (val.IntValue != 0) return val.IntValue.ToString()!;
        if (val.DoubleValue != 0) return val.DoubleValue.ToString()!;
        if (val.DecimalValue != 0) return val.DecimalValue.ToString()!;
        return "";
    }

    public static string Truncate(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength) return text;
        return text[..maxLength] + $"... ({text.Length} chars total)";
    }

    /// <summary>
    /// Вычисляет MD5 файла через SDK StreamExtensions.
    /// Возвращает hex-строку в том же формате, что использует Pilot (Md5Ext.ToOldMd5String).
    /// </summary>
    public static string ComputeFileMd5(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        var md5 = Ascon.Pilot.Common.Utils.StreamExtensions.ComputeMd5(stream, 81920);
        return Md5Ext.ToOldMd5String(md5);
    }

    public static Dictionary<string, DValue>? ParseAttributes(string json)
    {
        try
        {
            var dict = new Dictionary<string, DValue>();
            using var doc = JsonDocument.Parse(json);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                dict[prop.Name] = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => new DValue { StrValue = prop.Value.GetString()! },
                    JsonValueKind.Number when prop.Value.TryGetInt64(out var l) => new DValue { IntValue = l },
                    JsonValueKind.Number => new DValue { DoubleValue = prop.Value.GetDouble() },
                    JsonValueKind.True or JsonValueKind.False => new DValue { IntValue = prop.Value.GetBoolean() ? 1 : 0 },
                    _ => new DValue { StrValue = prop.Value.GetRawText() }
                };
            }
            return dict;
        }
        catch { return null; }
    }
}
