using System.ComponentModel;
using System.Text;
using Ascon.Pilot.DataClasses;
using ModelContextProtocol.Server;

namespace PilotMCP;

[McpServerToolType]
public class ReportTools
{
    private readonly PilotConnection _connection;
    public ReportTools(PilotConnection connection) => _connection = connection;

    // Путь к папке с примерами отчётов (рядом с exe)
    private static readonly string SamplesDir = Path.Combine(AppContext.BaseDirectory, "ReportSamples");

    // Системные имена типов и атрибутов для отчётов
    private const string ReportFolderTypeName = "Report_Folder_F2CC6F1D-70E1-4E9B-B32F-BEB3E991318F";
    private const string ReportTypeName = "Report_6088AF81-061E-456E-9225-CF65B7B25368";
    private const string ReportNameAttribute = "Report_name_D745D627-E7CD-4E3A-B30E-F9EDC1A09D77";

    private const string ReportApiReference =
        "СПРАВКА ПО PILOT REPORT API (.repx):\n" +
        "Формат: DevExpress XtraReports XML (SerializerVersion 20.1.8.0).\n" +
        "ВАЖНО: В скриптах используется C# версии 4 — НЕ используй string interpolation ($\"\"), " +
        "null-conditional (?.), pattern matching, expression-bodied members и т.д.\n" +
        "Используй string.Format(), обычные null-проверки, явные return.\n\n" +
        "Ключевые классы:\n" +
        "- ReportContext: CurrentPerson, GetType(name), GetTypes(), GetObject(id), GetObjects(builder, contextId?), " +
        "GetPeople(), GetOrganisationUnit(id), GetUserStates(), OpenFile(file), LoadChat(chatId), LoadMessages(...)\n" +
        "- RObject: Id, Type, Parent, Creator, CreatedUtc, Title, Attributes(dict), Children, Files, " +
        "Relations, Subscribers, SourceFiles, IsSecret, IsDeleted, IsInRecycleBin, Access, FilesSnapshots\n" +
        "- RType: Id, Name, Title, HasFiles, Kind, Attributes, Children\n" +
        "- RPerson: Id, Login, DisplayName, Comment, IsAdmin, MainPosition, Positions\n" +
        "- ROrganisationUnit: Id, Title, IsPosition, Person, Children\n" +
        "- RRelation: Id, Type(SourceFiles/TaskAttachments/Custom), Target, Name\n" +
        "- RChatInfo: Id, Name, Description, Creator, Type, LastMessageId\n" +
        "- RChatMessage: Id, Creator, Type, Date, Text, RelatedMessages\n\n" +
        "Поиск (QueryBuilder):\n" +
        "- QueryBuilder.CreateObjectQueryBuilder() — только неудалённые пользовательские типы\n" +
        "- QueryBuilder.CreateEmptyQueryBuilder() — пустой\n" +
        "- builder.Must(term) / MustNot(term) / MustAnyOf(terms)\n" +
        "- ObjectFields: Id.Be(guid), ParentId.Be(guid), TypeId.Be(id), CreatorId.Be(id), " +
        "CreatedDate.BeInRange(from,to), IsSecret.Be(bool), AllText.Be(\"text*\")\n" +
        "- AttributeFields: String(name).Be(val), Integer(name).Be(val), " +
        "Double(name).BeInRange(a,b), DateTime(name).BeInRange(from,to), " +
        "DateTime(name).Exists(), OrgUnit(name).Be(id), State(name).Be(guid)\n\n" +
        "Структура .repx:\n" +
        "- Корень: XtraReportsLayoutSerializer с ScriptsSource (C# код)\n" +
        "- Extensions: всегда 3 Item с PilotReportContext\n" +
        "- Parameters: входные параметры отчёта (RObject для выбора проекта и т.д.)\n" +
        "- Bands: ReportHeader, PageHeader, Detail, DetailReportBand, PageFooter, GroupHeader\n" +
        "- Controls: XRLabel, XRTable/XRTableRow/XRTableCell, XRPageInfo, XRPictureBox\n" +
        "- ExpressionBindings: привязки [FieldName], Iif(), форматирование\n" +
        "- CalculatedFields: вычисляемые поля с Expression или Scripts.OnGetValue\n" +
        "- StyleSheet: именованные стили\n" +
        "- Ref: уникальная последовательная нумерация всех элементов\n\n" +
        "Паттерн скрипта:\n" +
        "ReportContext context = new ReportContext();\n" +
        "private void PilotReport_DataSourceDemanded(object sender, EventArgs e) {\n" +
        "  LongRunning.Start(this, () => {\n" +
        "    var builder = QueryBuilder.CreateObjectQueryBuilder();\n" +
        "    builder.Must(...);\n" +
        "    context.Objects = context.GetObjects(builder);\n" +
        "    DataSource = context; // или DataSource = customList;\n" +
        "  });\n" +
        "}";

    /// <summary>
    /// Ищет папку отчётов (тип Report_Folder_*).
    /// </summary>
    private async Task<DObject?> FindReportFolderObjectAsync()
    {
        var reportFolderType = _connection.Metadata.Types
            .FirstOrDefault(t => t.Name == ReportFolderTypeName);
        if (reportFolderType == null) return null;

        var globalRoot = await FindGlobalRootAsync();
        if (globalRoot == null) return null;

        var folderChildIds = globalRoot.Children
            .Where(c => c.TypeId == reportFolderType.Id)
            .Select(c => c.ObjectId).ToArray();
        if (folderChildIds.Length == 0) return null;

        var folders = await _connection.ServerApi.GetObjectsAsync(folderChildIds);
        return folders.Count > 0 ? folders[0] : null;
    }

    /// <summary>
    /// Находит Global Root (Root_object_type) через Context стандартного корня.
    /// </summary>
    private async Task<DObject?> FindGlobalRootAsync()
    {
        var standardRootId = Guid.Parse("00000001-0001-0001-0001-000000000001");
        var roots = await _connection.ServerApi.GetObjectsAsync(new[] { standardRootId });
        if (roots.Count == 0 || !roots[0].Context.Any())
            return null;

        var globalRootId = roots[0].Context[0];
        var globalRoots = await _connection.ServerApi.GetObjectsAsync(new[] { globalRootId });
        return globalRoots.Count > 0 ? globalRoots[0] : null;
    }

    [McpServerTool, Description(
        "Найти папку отчётов в базе Pilot и показать все отчёты (рекурсивно по вложенным папкам). " +
        "Автоматически находит корневую папку типа Report_Folder. " +
        "Возвращает дерево папок и отчётов с GUID.")]
    public Task<string> FindReports()
    {
        return ToolRunner.RunAsync(_connection, "FindReports", "", async () =>
        {
            var folder = await FindReportFolderObjectAsync();
            if (folder == null)
                return ToolResult.Error(
                    "Папка отчётов не найдена в базе. " +
                    $"Искался тип: {ReportFolderTypeName}");

            var reportFolderTypeId = _connection.Metadata.Types
                .FirstOrDefault(t => t.Name == ReportFolderTypeName)?.Id ?? -1;

            var sb = new StringBuilder();
            sb.AppendLine($"Папка отчётов:");
            sb.AppendLine($"  ID: {folder.Id}");
            sb.AppendLine();

            await ListReportsRecursiveAsync(folder, sb, reportFolderTypeId, indent: 0);

            sb.AppendLine();
            sb.AppendLine("Для чтения отчёта: GetReport(<GUID>)");
            sb.AppendLine($"Для создания: CreateReport(reportName, repxContent, parentId=\"{folder.Id}\")");

            return ToolResult.Ok(sb.ToString());
        });
    }

    /// <summary>
    /// Рекурсивно обходит папки отчётов и выводит содержимое.
    /// </summary>
    private async Task ListReportsRecursiveAsync(DObject folder, StringBuilder sb, int reportFolderTypeId, int indent)
    {
        if (!folder.Children.Any())
        {
            sb.AppendLine($"{new string(' ', indent * 2)}(пусто)");
            return;
        }

        var childIds = folder.Children.Select(c => c.ObjectId).ToArray();
        var children = await _connection.ServerApi.GetObjectsAsync(childIds);

        foreach (var child in children.Where(c => c.TypeId == reportFolderTypeId))
        {
            var name = Helpers.GetDisplayName(child);
            sb.AppendLine($"{new string(' ', indent * 2)}[Папка] {name} ({child.Id})");
            await ListReportsRecursiveAsync(child, sb, reportFolderTypeId, indent + 1);
        }

        foreach (var child in children.Where(c => c.TypeId != reportFolderTypeId))
        {
            var name = Helpers.GetDisplayName(child);
            var hasRepx = child.ActualFileSnapshot.Files
                .Any(f => f.Name.EndsWith(".repx", StringComparison.OrdinalIgnoreCase));
            var repxInfo = hasRepx ? " [.repx]" : "";
            sb.AppendLine($"{new string(' ', indent * 2)}  - {child.Id}: {name}{repxInfo}");
        }
    }

    [McpServerTool, Description(
        "Прочитать отчёт (.repx) из объекта Pilot. Возвращает полный XML отчёта " +
        "для анализа, правки или использования как образца. " +
        "Чтобы найти отчёты в базе, вызови FindReports.")]
    public Task<string> GetReport(
        [Description("GUID объекта-отчёта в Pilot")] string objectId)
    {
        return ToolRunner.RunAsync(_connection, "GetReport", $"obj={objectId}", async () =>
        {
            if (!Guid.TryParse(objectId, out var guid))
                return ToolResult.Error($"Некорректный GUID: {objectId}");

            var objects = await _connection.ServerApi.GetObjectsAsync(new[] { guid });
            if (objects == null || objects.Count == 0)
                return ToolResult.Error($"Объект {objectId} не найден.");

            var obj = objects[0];
            var repxFile = obj.ActualFileSnapshot.Files
                .FirstOrDefault(f => f.Name.EndsWith(".repx", StringComparison.OrdinalIgnoreCase));

            if (repxFile == null)
            {
                if (obj.Children.Any())
                {
                    var childIds = obj.Children.Select(c => c.ObjectId).ToArray();
                    var children = await _connection.ServerApi.GetObjectsAsync(childIds);
                    foreach (var child in children)
                    {
                        repxFile = child.ActualFileSnapshot.Files
                            .FirstOrDefault(f => f.Name.EndsWith(".repx", StringComparison.OrdinalIgnoreCase));
                        if (repxFile != null) break;
                    }
                }
                if (repxFile == null)
                    return ToolResult.Error("Файл .repx не найден в объекте или его дочерних.");
            }

            var savePath = Helpers.DownloadToTemp(_connection.FileApi, repxFile.Body.Id, ".repx");
            var content = File.ReadAllText(savePath, Encoding.UTF8);

            if (content.Length > 15000)
            {
                return ToolResult.Ok(
                    $"Отчёт {repxFile.Name} ({content.Length} символов) сохранён: {savePath}\n" +
                    "Файл большой. Используйте PreviewFile для просмотра начала, " +
                    "или читайте файл целиком при необходимости правки.");
            }
            else
            {
                try { File.Delete(savePath); } catch { }
                return ToolResult.Ok($"Отчёт {repxFile.Name} ({content.Length} символов):\n\n{content}");
            }
        });
    }

    [McpServerTool, Description(
        "Обновить содержимое отчёта (.repx) в существующем объекте Pilot. " +
        "Принимает полный XML отчёта. Используй GetReport чтобы прочитать текущий, " +
        "GetReportSample для изучения примеров, затем UpdateReport для сохранения.")]
    public Task<string> UpdateReport(
        [Description("GUID объекта, содержащего .repx файл")] string objectId,
        [Description("Полный XML-контент .repx файла")] string repxContent)
    {
        return ToolRunner.RunWriteAsync(_connection, "UpdateReport",
            $"obj={objectId}, contentLen={repxContent?.Length ?? 0}", async () =>
        {
            if (!Guid.TryParse(objectId, out var guid))
                return ToolResult.Error($"Некорректный GUID: {objectId}");
            if (string.IsNullOrEmpty(repxContent))
                return ToolResult.Error("Пустой контент .repx.");

            var objects = await _connection.ServerApi.GetObjectsAsync(new[] { guid });
            if (objects == null || objects.Count == 0)
                return ToolResult.Error($"Объект {objectId} не найден.");

            var obj = objects[0];
            var repxFile = obj.ActualFileSnapshot.Files
                .FirstOrDefault(f => f.Name.EndsWith(".repx", StringComparison.OrdinalIgnoreCase));

            if (repxFile == null)
                return ToolResult.Error("Файл .repx не найден в объекте. Используйте CreateReport для создания.");

            var tempPath = Path.Combine(Path.GetTempPath(), "pilot-mcp", $"report_{Guid.NewGuid():N}.repx");
            Directory.CreateDirectory(Path.GetDirectoryName(tempPath)!);
            File.WriteAllText(tempPath, repxContent, Encoding.UTF8);

            try
            {
                var docInfo = new Ascon.Pilot.DataModifier.DocumentInfo(tempPath, repxFile.Name);
                var modifier = _connection.CreateModifier();
                modifier.EditObject(guid)
                    .CreateSnapshot("Report updated via MCP")
                    .AddFile(docInfo, _connection.StorageProvider);

                if (modifier.AnyChanges())
                    _connection.ApplyAndWait(() => modifier.Apply(null));
            }
            finally
            {
                DeleteTemp(tempPath);
            }

            return ToolResult.Ok($"Отчёт {repxFile.Name} обновлён ({repxContent.Length} символов).");
        });
    }

    [McpServerTool, Description(
        "Создать новый отчёт (.repx) в папке отчётов Pilot. " +
        "Автоматически находит папку отчётов и создаёт объект типа Report с .repx файлом.\n\n" +
        "ПЕРЕД СОЗДАНИЕМ: вызови GetReportSample чтобы изучить формат на примере.\n" +
        "Для просмотра существующих отчётов: вызови FindReports.\n\n")]
    public Task<string> CreateReport(
        [Description("Имя отчёта (будет установлено в атрибут name)")] string reportName,
        [Description("Полный XML-контент .repx файла")] string repxContent,
        [Description("Имя файла (например: 'Мой отчёт.repx'). Если пусто — формируется из reportName.")] string fileName = "",
        [Description("GUID папки отчётов. Если пусто — папка ищется автоматически.")] string parentId = "")
    {
        return ToolRunner.RunWriteAsync(_connection, "CreateReport", $"name={reportName}", async () =>
        {
            if (string.IsNullOrEmpty(repxContent))
                return ToolResult.Error("Пустой контент .repx.");

            if (string.IsNullOrEmpty(fileName))
                fileName = reportName + ".repx";
            if (!fileName.EndsWith(".repx", StringComparison.OrdinalIgnoreCase))
                fileName += ".repx";

            Guid parentGuid;
            if (!string.IsNullOrEmpty(parentId))
            {
                if (!Guid.TryParse(parentId, out parentGuid))
                    return ToolResult.Error($"Некорректный GUID родителя: {parentId}");
            }
            else
            {
                var folder = await FindReportFolderObjectAsync();
                if (folder == null)
                    return ToolResult.Error(
                        $"Папка отчётов (тип {ReportFolderTypeName}) не найдена автоматически. " +
                        "Укажите parentId явно.");
                parentGuid = folder.Id;
            }

            var reportType = _connection.Metadata.Types
                .FirstOrDefault(t => t.Name == ReportTypeName);
            int typeId;
            if (reportType != null)
            {
                typeId = reportType.Id;
            }
            else
            {
                var parents = await _connection.ServerApi.GetObjectsAsync(new[] { parentGuid });
                if (parents == null || parents.Count == 0)
                    return ToolResult.Error($"Родитель {parentGuid} не найден.");

                var parentType = _connection.Metadata.Types.FirstOrDefault(t => t.Id == parents[0].TypeId);
                if (parentType == null)
                    return ToolResult.Error("Не удалось определить тип родителя.");

                var childType = parentType.Children
                    .Select(childTypeId => _connection.Metadata.Types.FirstOrDefault(t => t.Id == childTypeId))
                    .FirstOrDefault(t => t != null && t.HasFiles);
                if (childType == null)
                    return ToolResult.Error($"Не найден подходящий дочерний тип для {parentType.Name}.");
                typeId = childType.Id;
            }

            var tempPath = Path.Combine(Path.GetTempPath(), "pilot-mcp", $"report_{Guid.NewGuid():N}.repx");
            Directory.CreateDirectory(Path.GetDirectoryName(tempPath)!);
            File.WriteAllText(tempPath, repxContent, Encoding.UTF8);

            var newId = Guid.NewGuid();
            try
            {
                var docInfo = new Ascon.Pilot.DataModifier.DocumentInfo(tempPath, fileName);
                var modifier = _connection.CreateModifier();
                modifier.CreateObject(newId, parentGuid, typeId)
                    .SetAttribute(ReportNameAttribute, new DValue { StrValue = reportName })
                    .AddFile(docInfo, _connection.StorageProvider);

                if (modifier.AnyChanges())
                    _connection.ApplyAndWait(() => modifier.Apply(null));
            }
            finally
            {
                DeleteTemp(tempPath);
            }

            return ToolResult.Ok(
                $"Отчёт создан: {reportName}\nID: {newId}\nФайл: {fileName}");
        });
    }

    private static void DeleteTemp(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ToolLogger.Log("ReportTools", "delete", $"Не удалось удалить {path}: {ex.Message}");
        }
    }

    [McpServerTool, Description(
        "Список доступных примеров отчётов (.repx). " +
        "Используй перед созданием нового отчёта — выбери подходящий пример и вызови GetReportSample.")]
    public string ListReportSamples()
    {
        try
        {
            if (!Directory.Exists(SamplesDir))
                return ToolResult.Error($"Папка с примерами не найдена: {SamplesDir}");

            var files = Directory.GetFiles(SamplesDir, "*.repx");
            if (files.Length == 0)
                return ToolResult.Ok("Примеры отчётов не найдены.");

            var sb = new StringBuilder();
            sb.AppendLine($"Доступные примеры отчётов ({files.Length}):");
            foreach (var f in files.OrderBy(f => f))
                sb.AppendLine($"  - {Path.GetFileNameWithoutExtension(f)}");

            sb.AppendLine();
            sb.AppendLine("Вызови GetReportSample с именем примера для изучения формата.");

            return ToolResult.Ok(sb.ToString());
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"Ошибка: {ex.Message}");
        }
    }

    [McpServerTool, Description(
        "Прочитать пример отчёта (.repx) для изучения формата и использования как образца. " +
        "Вызови ListReportSamples чтобы увидеть доступные примеры.\n\n")]
    public string GetReportSample(
        [Description("Имя примера (без расширения .repx), например: 'Отчет о моих текущих работах'")] string sampleName)
    {
        try
        {
            if (!Directory.Exists(SamplesDir))
                return ToolResult.Error($"Папка с примерами не найдена: {SamplesDir}");

            if (string.IsNullOrWhiteSpace(sampleName) || Path.GetFileName(sampleName) != sampleName)
                return ToolResult.Error("Имя примера не должно содержать путь.");

            var path = Path.Combine(SamplesDir, sampleName + ".repx");
            if (!File.Exists(path))
            {
                var files = Directory.GetFiles(SamplesDir, "*.repx");
                var match = files.FirstOrDefault(f =>
                    Path.GetFileNameWithoutExtension(f)
                        .Contains(sampleName, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                    path = match;
                else
                    return ToolResult.Error(
                        $"Пример '{sampleName}' не найден. Вызови ListReportSamples для списка.");
            }

            var content = File.ReadAllText(path, Encoding.UTF8);

            var sb = new StringBuilder();
            sb.AppendLine($"Пример отчёта: {Path.GetFileNameWithoutExtension(path)}");
            sb.AppendLine($"Размер: {content.Length} символов");
            sb.AppendLine($"Файл: {path}");
            sb.AppendLine();
            sb.AppendLine(ReportApiReference);

            if (content.Length > 15000)
            {
                sb.AppendLine();
                sb.AppendLine("Файл большой. Используйте PreviewFile для просмотра начала, " +
                    "или читайте файл целиком при необходимости.");
            }
            else
            {
                sb.AppendLine();
                sb.AppendLine("=== СОДЕРЖИМОЕ .repx ===");
                sb.AppendLine(content);
            }

            return ToolResult.Ok(sb.ToString());
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"Ошибка: {ex.Message}");
        }
    }

}
