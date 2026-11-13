using Ascon.Pilot.DataModifier;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace PilotMCP;

[McpServerToolType]
public class FileUploadTools
{
    private readonly PilotConnection _connection;
    public FileUploadTools(PilotConnection connection) => _connection = connection;

    [McpServerTool, Description(
        "Загрузить файл в существующий объект Pilot. Если у объекта уже есть файлы, создаётся новая версия: " +
        "прежняя версия уходит в историю, а в актуальной остаются все прежние файлы, кроме файла с тем же именем (он заменяется загружаемым). " +
        "Если файлов не было — файл просто добавляется, лишней пустой версии не создаётся.")]
    public Task<string> UploadFile(
        [Description("GUID объекта, к которому прикрепить файл")] string objectId,
        [Description("Путь к файлу на диске")] string filePath)
    {
        return ToolRunner.RunWriteAsync(_connection, "UploadFile", $"obj={objectId}, file={filePath}", async () =>
        {
            if (!Guid.TryParse(objectId, out var objGuid))
                return ToolResult.Error($"Некорректный GUID: {objectId}");

            if (!File.Exists(filePath))
                return ToolResult.Error($"Файл не найден: {filePath}");

            var objects = await _connection.ServerApi.GetObjectsAsync(new[] { objGuid });
            if (objects == null || objects.Count == 0)
                return ToolResult.Error($"Объект {objectId} не найден.");

            var fileName = Path.GetFileName(filePath);
            var existing = objects[0].ActualFileSnapshot.Files;
            var docInfo = new DocumentInfo(filePath);
            var modifier = _connection.CreateModifier();
            var builder = modifier.EditObject(objGuid);
            if (existing.Count > 0)
            {
                // CreateSnapshot очищает актуальный набор файлов — возвращаем остальные файлы.
                builder.CreateSnapshot("File upload via MCP");
                foreach (var file in existing.Where(f => !f.Name.Equals(fileName, StringComparison.OrdinalIgnoreCase)))
                    builder.AddExistingFile(file.Clone());
            }
            builder.AddFile(docInfo, _connection.StorageProvider);

            if (!modifier.AnyChanges())
                return ToolResult.Error("Нет изменений для применения: файл не загружен.");
            _connection.ApplyAndWait(() => modifier.Apply(null));

            return ToolResult.Ok(
                $"Файл загружен: {Path.GetFileName(filePath)} ({new FileInfo(filePath).Length} байт)\nОбъект: {objectId}");
        });
    }
}
