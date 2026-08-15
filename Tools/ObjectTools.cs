using Ascon.Pilot.DataClasses;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text;

namespace PilotMCP;

// =====================================================
// Подключение и метаданные
// =====================================================

[McpServerToolType]
public class ConnectionTools
{
    private readonly PilotConnection _connection;
    public ConnectionTools(PilotConnection connection) => _connection = connection;

    [McpServerTool, Description("Получить информацию о текущей базе данных Pilot. Работает из кэша.")]
    public string GetDatabaseInfo()
    {
        var info = _connection.DatabaseInfo;
        return ToolResult.Ok($"База: {_connection.DatabaseName}, версия метаданных: {info.MetadataVersion}");
    }
}

// =====================================================
// CRUD-операции с объектами
// =====================================================

[McpServerToolType]
public class ObjectTools
{
    private readonly PilotConnection _connection;
    public ObjectTools(PilotConnection connection) => _connection = connection;

    [McpServerTool, Description("Получить один объект по GUID. brief=true для краткой сводки (id, тип, имя, количество детей/файлов). Для массовых запросов используйте search или get_children.")]
    public Task<string> GetObject(
        [Description("GUID идентификатор объекта")] string objectId,
        [Description("Краткий режим — только основные поля без атрибутов, связей и файлов (экономит контекст)")] bool brief = false)
    {
        return ToolRunner.RunAsync(_connection, "GetObject", $"obj={objectId}, brief={brief}", async () =>
        {
            if (!Guid.TryParse(objectId, out var guid))
                return ToolResult.Error($"Некорректный GUID: {objectId}");

            var objects = await _connection.ServerApi.GetObjectsAsync(new[] { guid });
            if (objects == null || objects.Count == 0)
                return ToolResult.Error($"Объект {objectId} не найден.");

            return brief ? FormatObjectBrief(objects[0]) : FormatObjectFull(objects[0]);
        });
    }

    [McpServerTool, Description("Получить информацию о типе объекта по имени.")]
    public string GetType(
        [Description("Системное имя типа")] string typeName)
    {
        var type = _connection.Metadata.Types.FirstOrDefault(t =>
            t.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase));

        if (type == null)
            return ToolResult.Error($"Тип '{typeName}' не найден.");

        return FormatType(type);
    }

    [McpServerTool, Description("Создать новый объект в Pilot с одним начальным атрибутом. Для установки дополнительных атрибутов вызовите SetAttribute несколько раз.")]
    public Task<string> CreateObject(
        [Description("GUID родительского объекта. Корень базы: 00000001-0001-0001-0001-000000000001")] string parentId,
        [Description("Системное имя типа объекта")] string typeName,
        [Description("Имя атрибута, например: name")] string attributeName,
        [Description("Значение атрибута")] string attributeValue)
    {
        return ToolRunner.RunWriteAsync(_connection, "CreateObject", $"parent={parentId}, type={typeName}", async () =>
        {
            if (!Guid.TryParse(parentId, out var parentGuid))
                return ToolResult.Error($"Некорректный GUID родителя: {parentId}");

            var type = _connection.Metadata.Types.FirstOrDefault(t =>
                t.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase));
            if (type == null)
                return ToolResult.Error($"Тип '{typeName}' не найден.");

            var attrs = new Dictionary<string, DValue>
            {
                [attributeName] = new DValue { StrValue = attributeValue }
            };
            var newId = Helpers.CreateChildObject(_connection, parentGuid, type.Id, attrs);
            return ToolResult.Ok($"Объект создан. ID: {newId}");
        });
    }

    [McpServerTool, Description("Прочитать полное значение одного атрибута без обрезки. Используйте когда GetObject обрезал значение (показал '... (N chars total)').")]
    public Task<string> GetAttribute(
        [Description("GUID объекта")] string objectId,
        [Description("Имя атрибута")] string attributeName)
    {
        return ToolRunner.RunAsync(_connection, "GetAttribute", $"obj={objectId}, attr={attributeName}", async () =>
        {
            if (!Guid.TryParse(objectId, out var guid))
                return ToolResult.Error($"Некорректный GUID: {objectId}");

            var objects = await _connection.ServerApi.GetObjectsAsync(new[] { guid });
            if (objects == null || objects.Count == 0)
                return ToolResult.Error($"Объект {objectId} не найден.");

            var obj = objects[0];
            if (!obj.Attributes.TryGetValue(attributeName, out var val))
                return ToolResult.Error($"Атрибут '{attributeName}' не найден. Доступные: {string.Join(", ", obj.Attributes.Keys)}");

            var fullValue = Helpers.FormatDValue(val, maxLength: 0);
            if (string.IsNullOrEmpty(fullValue))
                return ToolResult.Ok($"{attributeName} = (пусто)");

            if (fullValue.Length > 10000)
            {
                var path = TempFiles.NewPath("attr");
                File.WriteAllText(path, fullValue);
                return ToolResult.Ok($"{attributeName} ({fullValue.Length} символов) сохранён: {path}");
            }

            return ToolResult.Ok($"{attributeName} = {fullValue}");
        });
    }

    [McpServerTool, Description("Установить значение одного атрибута объекта. Для изменения нескольких атрибутов вызовите этот инструмент несколько раз.")]
    public Task<string> SetAttribute(
        [Description("GUID объекта")] string objectId,
        [Description("Имя атрибута, например: code, name, title")] string attributeName,
        [Description("Новое значение атрибута")] string attributeValue)
    {
        return ToolRunner.RunWriteAsync(_connection, "SetAttribute", $"obj={objectId}, attr={attributeName}", async () =>
        {
            if (!Guid.TryParse(objectId, out var guid))
                return ToolResult.Error($"Некорректный GUID: {objectId}");

            var modifier = _connection.CreateModifier();
            modifier.EditObject(guid).SetAttribute(attributeName, new DValue { StrValue = attributeValue });
            if (modifier.AnyChanges())
                modifier.Apply(null);

            return ToolResult.Ok($"Атрибут \"{attributeName}\" объекта {objectId} установлен в \"{attributeValue}\".");
        });
    }

    [McpServerTool, Description("Удалить объект (перемещение в корзину).")]
    public Task<string> DeleteObject(
        [Description("GUID объекта для удаления")] string objectId)
    {
        return ToolRunner.RunWriteAsync(_connection, "DeleteObject", $"obj={objectId}", async () =>
        {
            if (!Guid.TryParse(objectId, out var guid))
                return ToolResult.Error($"Некорректный GUID: {objectId}");

            var modifier = _connection.CreateModifier();
            modifier.MoveToRecycleBin(guid, _connection.StorageProvider);
            if (modifier.AnyChanges())
                modifier.Apply(null);

            return ToolResult.Ok($"Объект {objectId} удалён (перемещён в корзину).");
        });
    }

    [McpServerTool, Description("Восстановить объект из корзины. Возвращает объект в исходную родительскую папку.")]
    public Task<string> RestoreObject(
        [Description("GUID объекта в корзине")] string objectId)
    {
        return ToolRunner.RunWriteAsync(_connection, "RestoreObject", $"obj={objectId}", async () =>
        {
            if (!Guid.TryParse(objectId, out var guid))
                return ToolResult.Error($"Некорректный GUID: {objectId}");

            var objects = await _connection.ServerApi.GetObjectsAsync(new[] { guid });
            if (objects == null || objects.Count == 0)
                return ToolResult.Error($"Объект {objectId} не найден.");

            var obj = objects[0];
            if (obj.StateInfo?.State != ObjectState.InRecycleBin)
                return ToolResult.Error($"Объект не в корзине (состояние: {obj.StateInfo?.State}).");

            const string deleteSourceAttr = "Delete_source_EFED1D30-E3E2-49E1-BDF3-13C544DADE9F";
            if (!obj.Attributes.TryGetValue(deleteSourceAttr, out var parentVal)
                || !Guid.TryParse(parentVal.StrValue, out var parentId))
                return ToolResult.Error("Не удалось определить исходную папку: атрибут Delete_source отсутствует.");

            var modifier = _connection.CreateModifier();
            modifier.MoveFromRecycleBin(guid, parentId);
            if (modifier.AnyChanges())
                modifier.Apply(null);

            return ToolResult.Ok($"Объект {objectId} восстановлен из корзины.");
        });
    }

    [McpServerTool, Description("Скачать файл из объекта Pilot и сохранить на диск.")]
    public string DownloadFile(
        [Description("GUID тела файла (Body.Id из информации об объекте)")] string fileBodyId,
        [Description("Путь для сохранения файла на диске")] string savePath)
    {
        return ToolRunner.Run(_connection, "DownloadFile", $"body={fileBodyId}, path={savePath}", () =>
        {
            if (!Guid.TryParse(fileBodyId, out var bodyId))
                return ToolResult.Error($"Некорректный GUID: {fileBodyId}");

            var offset = Helpers.DownloadToFile(_connection.FileApi, bodyId, savePath);
            return ToolResult.Ok($"Файл сохранён: {savePath} ({offset} байт)");
        });
    }

    [McpServerTool, Description("Переместить объект в другую папку.")]
    public Task<string> MoveObject(
        [Description("GUID объекта")] string objectId,
        [Description("GUID новой родительской папки")] string newParentId)
    {
        return ToolRunner.RunWriteAsync(_connection, "MoveObject", $"obj={objectId}, parent={newParentId}", async () =>
        {
            if (!Guid.TryParse(objectId, out var guid))
                return ToolResult.Error($"Некорректный GUID: {objectId}");
            if (!Guid.TryParse(newParentId, out var parentGuid))
                return ToolResult.Error($"Некорректный GUID родителя: {newParentId}");

            var modifier = _connection.CreateModifier();
            modifier.Move(guid, parentGuid);
            if (modifier.AnyChanges()) modifier.Apply(null);
            return ToolResult.Ok($"Объект {objectId} перемещён в {newParentId}.");
        });
    }

    [McpServerTool, Description("Заблокировать объект (запросить блокировку для эксклюзивного редактирования).")]
    public Task<string> LockObject(
        [Description("GUID объекта")] string objectId)
    {
        return ToolRunner.RunWriteAsync(_connection, "LockObject", $"obj={objectId}", async () =>
        {
            if (!Guid.TryParse(objectId, out var guid))
                return ToolResult.Error($"Некорректный GUID: {objectId}");

            var modifier = _connection.CreateModifier();
            modifier.EditObject(guid).Lock();
            if (modifier.AnyChanges()) modifier.Apply(null);
            return ToolResult.Ok($"Объект {objectId} заблокирован.");
        });
    }

    [McpServerTool, Description("Разблокировать объект.")]
    public Task<string> UnlockObject(
        [Description("GUID объекта")] string objectId)
    {
        return ToolRunner.RunWriteAsync(_connection, "UnlockObject", $"obj={objectId}", async () =>
        {
            if (!Guid.TryParse(objectId, out var guid))
                return ToolResult.Error($"Некорректный GUID: {objectId}");

            var modifier = _connection.CreateModifier();
            modifier.EditObject(guid).Unlock();
            if (modifier.AnyChanges()) modifier.Apply(null);
            return ToolResult.Ok($"Объект {objectId} разблокирован.");
        });
    }

    [McpServerTool, Description("Заморозить объект (запретить редактирование).")]
    public Task<string> FreezeObject(
        [Description("GUID объекта")] string objectId)
    {
        return ToolRunner.RunWriteAsync(_connection, "FreezeObject", $"obj={objectId}", async () =>
        {
            if (!Guid.TryParse(objectId, out var guid))
                return ToolResult.Error($"Некорректный GUID: {objectId}");

            var modifier = _connection.CreateModifier();
            modifier.EditObject(guid).SetState(ObjectState.Frozen);
            if (modifier.AnyChanges()) modifier.Apply(null);
            return ToolResult.Ok($"Объект {objectId} заморожен.");
        });
    }

    [McpServerTool, Description("Разморозить объект (разрешить редактирование).")]
    public Task<string> UnfreezeObject(
        [Description("GUID объекта")] string objectId)
    {
        return ToolRunner.RunWriteAsync(_connection, "UnfreezeObject", $"obj={objectId}", async () =>
        {
            if (!Guid.TryParse(objectId, out var guid))
                return ToolResult.Error($"Некорректный GUID: {objectId}");

            var modifier = _connection.CreateModifier();
            modifier.EditObject(guid).SetState(ObjectState.Alive);
            if (modifier.AnyChanges()) modifier.Apply(null);
            return ToolResult.Ok($"Объект {objectId} разморожен.");
        });
    }

    [McpServerTool, Description("Сделать объект секретным (ограниченный доступ).")]
    public Task<string> MakeSecret(
        [Description("GUID объекта")] string objectId)
    {
        return ToolRunner.RunWriteAsync(_connection, "MakeSecret", $"obj={objectId}", async () =>
        {
            if (!Guid.TryParse(objectId, out var guid))
                return ToolResult.Error($"Некорректный GUID: {objectId}");

            var modifier = _connection.CreateModifier();
            modifier.EditObject(guid).MakeSecret();
            if (modifier.AnyChanges()) modifier.Apply(null);
            return ToolResult.Ok($"Объект {objectId} помечен как секретный.");
        });
    }

    [McpServerTool, Description("Снять секретность с объекта (сделать публичным).")]
    public Task<string> MakePublic(
        [Description("GUID объекта")] string objectId)
    {
        return ToolRunner.RunWriteAsync(_connection, "MakePublic", $"obj={objectId}", async () =>
        {
            if (!Guid.TryParse(objectId, out var guid))
                return ToolResult.Error($"Некорректный GUID: {objectId}");

            var modifier = _connection.CreateModifier();
            modifier.EditObject(guid).MakePublic();
            if (modifier.AnyChanges()) modifier.Apply(null);
            return ToolResult.Ok($"Секретность объекта {objectId} снята.");
        });
    }

    [McpServerTool, Description("Удалить файл из объекта по его Body.Id.")]
    public Task<string> DeleteFile(
        [Description("GUID объекта")] string objectId,
        [Description("GUID файла (Body.Id)")] string fileId)
    {
        return ToolRunner.RunWriteAsync(_connection, "DeleteFile", $"obj={objectId}, file={fileId}", async () =>
        {
            if (!Guid.TryParse(objectId, out var objGuid))
                return ToolResult.Error($"Некорректный GUID объекта: {objectId}");
            if (!Guid.TryParse(fileId, out var fGuid))
                return ToolResult.Error($"Некорректный GUID файла: {fileId}");

            var modifier = _connection.CreateModifier();
            modifier.EditObject(objGuid).DeleteFile(fGuid);
            if (modifier.AnyChanges()) modifier.Apply(null);
            return ToolResult.Ok($"Файл {fileId} удалён из объекта {objectId}.");
        });
    }

    [McpServerTool, Description("Подписаться на уведомления об изменениях объекта.")]
    public Task<string> Subscribe(
        [Description("GUID объекта")] string objectId,
        [Description("ID пользователя (personId). Если 0 — текущий пользователь.")] int personId = 0)
    {
        return ToolRunner.RunWriteAsync(_connection, "Subscribe", $"obj={objectId}, person={personId}", async () =>
        {
            if (!Guid.TryParse(objectId, out var guid))
                return ToolResult.Error($"Некорректный GUID: {objectId}");

            if (personId == 0) personId = _connection.CurrentPersonId;
            var modifier = _connection.CreateModifier();
            modifier.EditObject(guid).AddSubscriber(personId);
            if (modifier.AnyChanges()) modifier.Apply(null);
            return ToolResult.Ok($"Пользователь #{personId} подписан на объект {objectId}.");
        });
    }

    [McpServerTool, Description("Отписаться от уведомлений об изменениях объекта.")]
    public Task<string> Unsubscribe(
        [Description("GUID объекта")] string objectId,
        [Description("ID пользователя (personId). Если 0 — текущий пользователь.")] int personId = 0)
    {
        return ToolRunner.RunWriteAsync(_connection, "Unsubscribe", $"obj={objectId}, person={personId}", async () =>
        {
            if (!Guid.TryParse(objectId, out var guid))
                return ToolResult.Error($"Некорректный GUID: {objectId}");

            if (personId == 0) personId = _connection.CurrentPersonId;
            var modifier = _connection.CreateModifier();
            modifier.EditObject(guid).RemoveSubscriber(personId);
            if (modifier.AnyChanges()) modifier.Apply(null);
            return ToolResult.Ok($"Пользователь #{personId} отписан от объекта {objectId}.");
        });
    }

    [McpServerTool, Description(
        "Установить пользовательское состояние (UserState) на объекте. " +
        "Используется для задач и workflow. Вызовите GetUserStates чтобы увидеть доступные состояния.")]
    public Task<string> SetUserState(
        [Description("GUID объекта")] string objectId,
        [Description("Имя атрибута состояния (обычно содержит 'state' в имени)")] string stateAttributeName,
        [Description("GUID состояния (из GetUserStates)")] string stateId)
    {
        return ToolRunner.RunWriteAsync(_connection, "SetUserState", $"obj={objectId}, attr={stateAttributeName}, state={stateId}", async () =>
        {
            if (!Guid.TryParse(objectId, out var guid))
                return ToolResult.Error($"Некорректный GUID: {objectId}");
            if (!Guid.TryParse(stateId, out var stateGuid))
                return ToolResult.Error($"Некорректный GUID состояния: {stateId}");

            var state = _connection.Metadata.UserStates.FirstOrDefault(s => s.Id == stateGuid);
            var stateName = state?.Title ?? stateId;

            var objects = await _connection.ServerApi.GetObjectsAsync(new[] { guid });
            if (objects == null || objects.Count == 0)
                return ToolResult.Error($"Объект {objectId} не найден.");

            var obj = objects[0];
            obj.Attributes.TryGetValue(stateAttributeName, out var currentStateValue);
            var currentStateStr = currentStateValue?.ToString() ?? "(не задано)";

            var modifier = _connection.CreateModifier();
            modifier.EditObject(guid).SetAttribute(stateAttributeName, stateGuid);

            if (!modifier.AnyChanges())
                return ToolResult.Error(
                    $"Нет изменений для применения. Текущее состояние атрибута '{stateAttributeName}': {currentStateStr}. " +
                    $"Запрошенное: {stateGuid}. Возможно, состояние уже установлено.");

            var changesetId = modifier.Apply(null);

            var updated = await _connection.ServerApi.GetObjectsAsync(new[] { guid });
            if (updated != null && updated.Count > 0)
            {
                updated[0].Attributes.TryGetValue(stateAttributeName, out var newVal);
                var newStr = newVal?.ToString() ?? "(не задано)";
                if (newStr != stateGuid.ToString())
                    return ToolResult.Error(
                        $"Изменение отправлено (changeset {changesetId}), но верификация не прошла. " +
                        $"Ожидалось: {stateGuid}, фактически: {newStr}. " +
                        $"Возможно, сервер отклонил изменение (бизнес-правила, state machine).");
            }

            return ToolResult.Ok(
                $"Состояние объекта {objectId} изменено на '{stateName}'.\n" +
                $"Атрибут: {stateAttributeName}, было: {currentStateStr}, стало: {stateGuid}.\n" +
                $"Changeset: {changesetId}");
        });
    }

    [McpServerTool, Description("Проверить размер файла в архиве Pilot (без скачивания).")]
    public string GetFileSize(
        [Description("GUID тела файла (Body.Id)")] string fileBodyId)
    {
        return ToolRunner.Run(_connection, "GetFileSize", $"body={fileBodyId}", () =>
        {
            if (!Guid.TryParse(fileBodyId, out var bodyId))
                return ToolResult.Error($"Некорректный GUID: {fileBodyId}");

            var position = _connection.FileApi.GetFilePosition(bodyId);
            return ToolResult.Ok($"Размер файла {fileBodyId} в архиве: {position} байт ({position / 1024} KB).");
        });
    }

    [McpServerTool, Description(
        "Вычислить MD5-хэш файла: из архива Pilot (по GUID тела файла, Body.Id) или локального файла (по пути на диске). " +
        "Возвращает hex-строку в том же формате, что использует Pilot.")]
    public string ComputeFileMd5(
        [Description("GUID тела файла (Body.Id) или путь к локальному файлу")] string fileBodyIdOrPath)
    {
        return ToolRunner.Run(_connection, "ComputeFileMd5", $"src={fileBodyIdOrPath}", () =>
        {
            if (Guid.TryParse(fileBodyIdOrPath, out var bodyId))
            {
                var tempPath = Helpers.DownloadToTemp(_connection.FileApi, bodyId, ".bin");
                try
                {
                    var size = new FileInfo(tempPath).Length;
                    if (size == 0)
                        return ToolResult.Error($"Файл {bodyId} в архиве пуст или отсутствует (скачано 0 байт).");

                    var md5 = Helpers.ComputeFileMd5(tempPath);
                    return ToolResult.Ok($"MD5 файла {bodyId} из архива: {md5} (размер: {size} байт)");
                }
                finally
                {
                    File.Delete(tempPath);
                }
            }

            if (!File.Exists(fileBodyIdOrPath))
                return ToolResult.Error(
                    $"Файл не найден: {fileBodyIdOrPath}. Укажите GUID тела файла (Body.Id) или путь к существующему локальному файлу.");

            var hash = Helpers.ComputeFileMd5(fileBodyIdOrPath);
            return ToolResult.Ok($"MD5 файла {fileBodyIdOrPath}: {hash} (размер: {new FileInfo(fileBodyIdOrPath).Length} байт)");
        });
    }

    // --- inline formatters ---

    private string FormatObjectBrief(DObject obj)
    {
        var typeName = Helpers.GetTypeName(_connection.Metadata, obj.TypeId);
        var name = Helpers.GetDisplayName(obj, _connection.Metadata);
        var sb = new StringBuilder();
        sb.AppendLine($"# {name}");
        sb.AppendLine($"ID: {obj.Id}");
        sb.AppendLine($"Тип: {typeName} (TypeId: {obj.TypeId})");
        sb.AppendLine($"Родитель: {obj.ParentId}");
        sb.AppendLine($"Создан: {obj.Created:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Дочерних: {obj.Children.Count}, Файлов: {obj.ActualFileSnapshot.Files.Count}, " +
                      $"Атрибутов: {obj.Attributes.Count}, Связей: {obj.Relations.Count}");
        if (obj.StateInfo != null && obj.StateInfo.State != ObjectState.Alive)
            sb.AppendLine($"Состояние: {obj.StateInfo.State}");
        return sb.ToString();
    }

    private string FormatObjectFull(DObject obj)
    {
        var typeName = Helpers.GetTypeName(_connection.Metadata, obj.TypeId);
        var name = Helpers.GetDisplayName(obj, _connection.Metadata);
        var sb = new StringBuilder();

        sb.AppendLine($"# {name}");
        sb.AppendLine($"ID: {obj.Id}");
        sb.AppendLine($"Тип: {typeName} (TypeId: {obj.TypeId})");
        sb.AppendLine($"Родитель: {obj.ParentId}");
        sb.AppendLine($"Создан: {obj.Created:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Создатель (personId): {obj.CreatorId}");
        sb.AppendLine($"LastChange (changesetId): {obj.LastChange}");

        if (obj.StateInfo != null)
        {
            sb.AppendLine($"Состояние: {obj.StateInfo.State}");
            if (obj.StateInfo.State != ObjectState.Alive)
                sb.AppendLine($"  изменено: {obj.StateInfo.Date:yyyy-MM-dd HH:mm:ss}, " +
                              $"кем (personId): {obj.StateInfo.PersonId}, позиция (positionId): {obj.StateInfo.PositionId}");
        }

        if (obj.SecretInfo.IsSecret)
        {
            sb.AppendLine($"Секретность: изменена {obj.SecretInfo.SecretChangedTimestamp:yyyy-MM-dd HH:mm:ss}, " +
                          $"кем (personId): {obj.SecretInfo.SecretChangedBy}, " +
                          $"секретный родитель: {obj.SecretInfo.SecretParentId}");
        }

        if (obj.Context.Any())
            sb.AppendLine($"Путь к корню (Context): {string.Join(" → ", obj.Context)}");

        if (obj.Subscribers.Any())
            sb.AppendLine($"Подписчики (personIds): {string.Join(", ", obj.Subscribers)}");

        if (obj.Attributes.Any())
        {
            sb.AppendLine("\n## Атрибуты:");
            foreach (var attr in obj.Attributes)
                sb.AppendLine($"  {attr.Key} = {Helpers.FormatDValue(attr.Value)}");
        }

        if (obj.Children.Any())
        {
            sb.AppendLine($"\n## Дочерние объекты ({obj.Children.Count}):");
            foreach (var child in obj.Children.Take(20))
                sb.AppendLine($"  - {child.ObjectId} (тип: {Helpers.GetTypeName(_connection.Metadata, child.TypeId)})");
            if (obj.Children.Count > 20)
                sb.AppendLine($"  ... и ещё {obj.Children.Count - 20}. Используйте get_children для полного списка.");
        }

        if (obj.ActualFileSnapshot.Files.Any())
        {
            sb.AppendLine($"\n## Файлы (актуальный снапшот, {obj.ActualFileSnapshot.Files.Count} файлов, создан: {obj.ActualFileSnapshot.Created:yyyy-MM-dd HH:mm:ss}, creatorId: {obj.ActualFileSnapshot.CreatorId}):");
            foreach (var file in obj.ActualFileSnapshot.Files)
            {
                var sigInfo = file.HasSignatures ? $", подписей: {file.SignatureRequests?.Count ?? 0}" : "";
                var injInfo = file.HasInjections ? ", есть вставки" : "";
                var delInfo = file.IsDeleted ? " [удалён]" : "";
                sb.AppendLine($"  - {file.Name}{delInfo} (Body.Id: {file.Body.Id}, " +
                              $"размер: {file.Body.Size} байт, " +
                              $"изменён: {file.Body.Modified:yyyy-MM-dd HH:mm:ss}, " +
                              $"создан: {file.Body.Created:yyyy-MM-dd HH:mm:ss}" +
                              $"{sigInfo}{injInfo})");
            }
        }

        if (obj.PreviousFileSnapshots.Any())
        {
            sb.AppendLine($"\n## Предыдущие версии файлов ({obj.PreviousFileSnapshots.Count} снапшотов):");
            foreach (var snap in obj.PreviousFileSnapshots.Take(10))
            {
                var reason = !string.IsNullOrEmpty(snap.Reason) ? $", причина: {snap.Reason}" : "";
                var delInfo = snap.IsDeleted ? " [удалён]" : "";
                sb.AppendLine($"  - {snap.Created:yyyy-MM-dd HH:mm:ss}{delInfo} (creatorId: {snap.CreatorId}{reason}, файлов: {snap.Files.Count})");
            }
            if (obj.PreviousFileSnapshots.Count > 10)
                sb.AppendLine($"  ... и ещё {obj.PreviousFileSnapshots.Count - 10}");
        }

        if (obj.Relations.Any())
        {
            sb.AppendLine($"\n## Связи ({obj.Relations.Count}):");
            foreach (var rel in obj.Relations.Take(20))
            {
                var nameInfo = !string.IsNullOrEmpty(rel.Name) ? $", имя: \"{rel.Name}\"" : "";
                var versionInfo = rel.VersionId != default ? $", версия: {rel.VersionId:yyyy-MM-dd HH:mm:ss}" : "";
                sb.AppendLine($"  - Id: {rel.Id}, Тип: {rel.Type}, Цель: {rel.TargetId}{nameInfo}{versionInfo}");
            }
        }

        if (obj.HistoryItems.Any())
            sb.AppendLine($"\nВерсий (HistoryItems): {obj.HistoryItems.Count}. Используйте GetHistory для деталей.");

        if (obj.Access.Any())
        {
            sb.AppendLine($"\n## Права доступа ({obj.Access.Count} записей):");
            foreach (var rule in obj.Access.Take(20))
            {
                var inherited = rule.InheritanceSource != Guid.Empty ? $" (унаследовано от {rule.InheritanceSource})" : "";
                sb.AppendLine($"  - OrgUnit #{rule.OrgUnitId}: {rule.Access.AccessLevel}" +
                              $" ({rule.Access.Type}, наследование: {rule.Access.Inheritance}){inherited}");
            }
        }

        return sb.ToString();
    }

    private string FormatType(MType type)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Тип: {type.Name} (ID: {type.Id})");
        sb.AppendLine($"Заголовок: {type.Title}");
        sb.AppendLine($"Файловый: {type.HasFiles}, Проектный: {type.IsProjectFolder}");
        if (type.Attributes.Any())
        {
            sb.AppendLine("\n## Атрибуты:");
            foreach (var attr in type.Attributes)
                sb.AppendLine($"  - {attr.Name} ({attr.Type}): {attr.Title}");
        }
        return sb.ToString();
    }
}

// Подписи (SignatureRequest)
[McpServerToolType]
public class SignatureTools
{
    private readonly PilotConnection _connection;
    public SignatureTools(PilotConnection connection) => _connection = connection;

    [McpServerTool, Description(
        "Добавить запрос на подпись к файлу в объекте.")]
    public Task<string> AddSignatureRequest(
        [Description("GUID объекта")] string objectId,
        [Description("ID позиции подписанта (positionId)")] int positionId,
        [Description("Роль подписанта (например: 'Согласующий', 'Утверждающий')")] string role,
        [Description("Имя файла для подписи (если пусто — первый файл)")] string fileName = "")
    {
        return ToolRunner.RunWriteAsync(_connection, "AddSignatureRequest",
            $"obj={objectId}, pos={positionId}, role={role}", async () =>
        {
            if (!Guid.TryParse(objectId, out var guid))
                return ToolResult.Error($"Некорректный GUID: {objectId}");

            var objects = await _connection.ServerApi.GetObjectsAsync(new[] { guid });
            if (objects == null || objects.Count == 0)
                return ToolResult.Error($"Объект {objectId} не найден.");

            var obj = objects[0];
            if (!obj.ActualFileSnapshot.Files.Any())
                return ToolResult.Error("У объекта нет файлов.");

            Predicate<Ascon.Pilot.DataClasses.INFile> findFile = string.IsNullOrEmpty(fileName)
                ? _ => true
                : f => f.Name.Equals(fileName, StringComparison.OrdinalIgnoreCase);

            var request = new Ascon.Pilot.DataClasses.DSignatureRequest
            {
                Id = Guid.NewGuid(),
                DatabaseId = _connection.DatabaseInfo.DatabaseId,
                PositionId = positionId,
                Role = role,
                ObjectId = guid
            };

            var modifier = _connection.CreateModifier();
            modifier.EditObject(guid).AddSignatureRequest(findFile, request);
            if (modifier.AnyChanges()) modifier.Apply(null);
            return ToolResult.Ok($"Запрос на подпись добавлен: позиция #{positionId}, роль: {role}.");
        });
    }
}
