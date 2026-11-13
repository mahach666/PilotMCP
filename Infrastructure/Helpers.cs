using Ascon.Pilot.Common;
using Ascon.Pilot.DataClasses;
using Ascon.Pilot.Server.Api.Contracts;

namespace PilotMCP;

internal static partial class Helpers
{
    private static readonly string TempDocDir = Path.Combine(Path.GetTempPath(), "pilot-mcp");

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
        // без проверки SDK падает внутри CreateObject с NullReferenceException
        if (connection.ServerApi.GetObjectsAsync(new[] { parentId }).GetAwaiter().GetResult() is not { Count: > 0 })
            throw new ArgumentException($"Родительский объект {parentId} не найден.");

        var newId = predefinedId ?? Guid.NewGuid();
        var modifier = connection.CreateModifier();
        var builder = modifier.CreateObject(newId, parentId, typeId);

        if (attributes != null)
        {
            foreach (var kvp in attributes)
                builder.SetAttribute(kvp.Key, kvp.Value);
        }

        if (modifier.AnyChanges())
            connection.ApplyAndWait(() => modifier.Apply(null));
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
}
