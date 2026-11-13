using Ascon.Pilot.DataClasses;
using Ascon.Pilot.Server.Api.Contracts;
using System.Globalization;

namespace PilotMCP;

// Чистые функции для инструментов объектов — отдельным файлом, чтобы линковаться в unit-тесты.
internal static partial class Helpers
{
    /// <summary>
    /// Строит DValue из строки по типу атрибута (как это делает клиент Pilot: числа и даты — InvariantCulture).
    /// Неразбираемое значение → ArgumentException (инструмент вернёт ошибку).
    /// </summary>
    public static DValue BuildTypedValue(MAttribute attr, string value)
    {
        value ??= "";
        try
        {
            switch (attr.Type)
            {
                case MAttrType.Integer:
                    return new DValue { IntValue = long.Parse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture) };
                case MAttrType.Boolean:
                    return new DValue { IntValue = ParseBool(value) ? 1 : 0 };
                case MAttrType.Double:
                    return new DValue { DoubleValue = double.Parse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture) };
                case MAttrType.Decimal:
                    return new DValue { DecimalValue = decimal.Parse(value.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture) };
                case MAttrType.DateTime:
                    return new DValue
                    {
                        DateValue = DateTime.Parse(value.Trim(), CultureInfo.InvariantCulture,
                            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal)
                    };
                case MAttrType.UserState:
                    return new DValue { GuidValue = Guid.Parse(value.Trim()) };
                case MAttrType.OrgUnit:
                    return new DValue
                    {
                        ArrayIntValue = value.Split(new[] { ',', ';' }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                            .Select(s => int.Parse(s, NumberStyles.Integer, CultureInfo.InvariantCulture)).ToArray()
                    };
                case MAttrType.ElementBook:
                case MAttrType.Array:
                    return new DValue
                    {
                        ArrayValue = value.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    };
                case MAttrType.ByteArray:
                    throw new ArgumentException($"атрибут '{attr.Name}' типа ByteArray не поддерживается");
                default: // String, Numerator, Inherited
                    return new DValue { StrValue = value };
            }
        }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            throw new ArgumentException(
                $"Значение \"{value}\" не подходит для атрибута '{attr.Name}' типа {attr.Type}: {ex.Message}", ex);
        }
    }

    private static bool ParseBool(string value) => value.Trim().ToLowerInvariant() switch
    {
        "1" or "true" or "yes" or "да" => true,
        "0" or "false" or "no" or "нет" => false,
        _ => throw new FormatException("ожидается true/false или 1/0")
    };

    /// <summary>Размер файла в архиве: читает чанки (без записи на диск). 0 — файла нет или он пуст.</summary>
    public static long MeasureArchiveFile(IFileArchiveApi fileApi, Guid bodyId)
    {
        const int chunkSize = 1024 * 1024;
        long offset = 0;
        while (true)
        {
            var chunk = fileApi.GetFileChunk(bodyId, offset, chunkSize);
            if (chunk == null || chunk.Length == 0) break;
            offset += chunk.Length;
            if (chunk.Length < chunkSize) break;
        }
        return offset;
    }
}
