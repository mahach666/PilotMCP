using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;
using PilotMCP;

// Генерирует справочник инструментов docs/tools/*.md по сборке PilotMCP (рефлексия + разбор Tools/*.cs).
// Ненулевой код выхода — если признак «запись» из исходников расходится со списком PILOT_READONLY в README.md.

var root = FindRoot();
var outDir = Path.Combine(root, "docs", "tools");

// Класс -> (slug страницы, заголовок). Порядок = порядок групп; классы с одним slug сливаются в одну страницу.
var groupMap = new (string Class, string Slug, string Title)[]
{
    ("ConnectionTools", "connection", "Подключение"),
    ("ObjectTools", "objects", "Объекты"),
    ("FileUploadTools", "files", "Загрузка файлов"),
    ("QueryTools", "search", "Навигация и поиск"),
    ("PeopleTools", "people", "Пользователи и организация"),
    ("PeopleAdminTools", "people-admin", "Управление пользователями и оргструктурой"),
    ("TransformTools", "pipe", "Работа с файлами результатов"),
    ("InspectTools", "pipe", "Работа с файлами результатов"),
    ("ExportTools", "pipe", "Работа с файлами результатов"),
    ("DocumentTools", "documents", "Документы"),
    ("AnnotationTools", "annotations", "Замечания (аннотации)"),
    ("HistoryTools", "history", "История и версии"),
    ("AccessTools", "access", "Права доступа"),
    ("RelationTools", "relations", "Связи между объектами"),
    ("MessengerTools", "chats", "Чаты"),
    ("SettingsTools", "settings", "Настройки"),
    ("ReportTools", "reports", "Отчёты"),
    ("AdminTools", "admin", "Администрирование (пользователь, настройки)"),
    ("MetadataAdminTools", "metadata", "Метаданные и автоматизации"),
    ("TaskTools", "tasks", "Задачи"),
    ("SignatureTools", "signatures", "Подписи"),
    ("CommandTools", "commands", "Команды сервера"),
};

var writeTools = FindWriteTools(Path.Combine(root, "Tools"));

var tools = new List<Tool>();
foreach (var type in typeof(PilotConnection).Assembly.GetTypes().Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() != null))
{
    var (slug, title) = groupMap.Where(g => g.Class == type.Name).Select(g => (g.Slug, g.Title)).FirstOrDefault();
    if (slug == null) (slug, title) = (type.Name.ToLowerInvariant(), type.Name);
    foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
    {
        var attr = m.GetCustomAttribute<McpServerToolAttribute>();
        if (attr == null) continue;
        var name = attr.Name ?? m.Name;
        var ps = m.GetParameters().Select(p => new Param(
            p.Name!, TypeName(p.ParameterType), !p.HasDefaultValue,
            p.HasDefaultValue ? DefaultText(p.DefaultValue) : "",
            p.GetCustomAttribute<DescriptionAttribute>()?.Description ?? "")).ToList();
        // Метод в Tools/*.cs ищется по имени C#-метода, а не по имени инструмента
        var isWrite = writeTools.TryGetValue(type.Name + "." + m.Name, out var w)
            ? w
            : throw new InvalidOperationException($"Не найден исходный код метода {type.Name}.{m.Name} в Tools/*.cs");
        tools.Add(new Tool(name, type.Name, slug, title,
            m.GetCustomAttribute<DescriptionAttribute>()?.Description ?? "", ps, isWrite));
    }
}

var dup = tools.GroupBy(t => t.Name).FirstOrDefault(g => g.Count() > 1);
if (dup != null) Fail($"Повторяющееся имя инструмента: {dup.Key}");

// Сверка с README: список заблокированных в режиме PILOT_READONLY должен совпадать с найденными инструментами записи
var readme = File.ReadAllText(Path.Combine(root, "README.md"));
var blockedLine = readme.Split('\n').FirstOrDefault(l => l.StartsWith("Заблокированы:", StringComparison.Ordinal))
    ?? throw new InvalidOperationException("В README.md не найдена строка «Заблокированы:».");
var blocked = Regex.Matches(blockedLine, "`([^`]+)`").Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
var found = tools.Where(t => t.IsWrite).Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
var onlyCode = found.Except(blocked).OrderBy(x => x, StringComparer.Ordinal).ToList();
var onlyReadme = blocked.Except(found).OrderBy(x => x, StringComparer.Ordinal).ToList();
if (onlyCode.Count > 0 || onlyReadme.Count > 0)
    Fail("Список записи расходится с README.md (PILOT_READONLY).\n" +
         $"  Пишут в коде, но нет в README: {string.Join(", ", onlyCode)}\n" +
         $"  Есть в README, но не пишут в коде: {string.Join(", ", onlyReadme)}");

// Страницы
Directory.CreateDirectory(outDir);
foreach (var f in Directory.EnumerateFiles(outDir)) File.Delete(f);

var groups = tools
    .GroupBy(t => t.Slug)
    .OrderBy(g => GroupOrder(g.Key)).ThenBy(g => g.Key, StringComparer.Ordinal)
    .ToList();

foreach (var g in groups)
{
    var sb = new StringBuilder();
    sb.Append($"# {g.First().Title}\n\n");
    foreach (var t in g.OrderBy(t => t.Name, StringComparer.Ordinal))
    {
        sb.Append($"## {t.Name}\n\n");
        sb.Append(t.IsWrite
            ? "**Запись.** Блокируется режимом `PILOT_READONLY`.\n\n"
            : "**Чтение.**\n\n");
        sb.Append(Esc(t.Description)).Append("\n\n");
        if (t.Params.Count == 0)
        {
            sb.Append("Параметров нет.\n\n");
            continue;
        }
        sb.Append("| Параметр | Тип | Обязательный | По умолчанию | Описание |\n| --- | --- | --- | --- | --- |\n");
        foreach (var p in t.Params)
            sb.Append($"| `{p.Name}` | `{p.Type}` | {(p.Required ? "да" : "нет")} | {(p.Required ? "" : $"`{p.Default}`")} | {Esc(p.Description)} |\n");
        sb.Append('\n');
    }
    Write(g.Key + ".md", sb.ToString());
}

var idx = new StringBuilder();
idx.Append("# Справочник инструментов\n\n");
idx.Append($"Всего инструментов: {tools.Count}, из них записывающих: {tools.Count(t => t.IsWrite)}. ");
idx.Append("Записывающие инструменты блокируются режимом `PILOT_READONLY` (см. [Конфигурация](../configuration.md)). ");
idx.Append("Страницы справочника сформированы автоматически по коду сервера.\n\n");
idx.Append("| Инструмент | Группа | Тип | Описание |\n| --- | --- | --- | --- |\n");
foreach (var g in groups)
    foreach (var t in g.OrderBy(t => t.Name, StringComparer.Ordinal))
        idx.Append($"| [`{t.Name}`]({g.Key}.md#{t.Name.ToLowerInvariant()}) | {t.Title} | {(t.IsWrite ? "запись" : "чтение")} | {Esc(Short(t.Description))} |\n");
Write("index.md", idx.ToString());

var toc = new StringBuilder("- name: Все инструменты\n  href: index.md\n");
foreach (var g in groups)
    toc.Append($"- name: \"{g.First().Title}\"\n  href: {g.Key}.md\n");
Write("toc.yml", toc.ToString());

Console.WriteLine($"DocGen: {tools.Count} инструментов ({found.Count} записывающих), {groups.Count} групп -> {outDir}");
return 0;

void Write(string file, string text) =>
    File.WriteAllText(Path.Combine(outDir, file), text, new UTF8Encoding(false));

static void Fail(string message)
{
    Console.Error.WriteLine("DocGen: " + message);
    Environment.Exit(1);
}

int GroupOrder(string slug)
{
    var i = Array.FindIndex(groupMap, g => g.Slug == slug);
    return i < 0 ? int.MaxValue : i;
}

static string FindRoot()
{
    for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        if (File.Exists(Path.Combine(d.FullName, "PilotMCP.slnx"))) return d.FullName;
    throw new InvalidOperationException("Не найден корень репозитория (PilotMCP.slnx).");
}

// "Класс.Метод" -> вызывает ли тело ToolRunner.RunWrite/RunWriteAsync.
// Тело метода = текст от его [McpServerTool] до следующего [McpServerTool] / [McpServerToolType] / конца файла.
static Dictionary<string, bool> FindWriteTools(string toolsDir)
{
    var result = new Dictionary<string, bool>(StringComparer.Ordinal);
    foreach (var file in Directory.EnumerateFiles(toolsDir, "*.cs").OrderBy(f => f, StringComparer.Ordinal))
    {
        var text = File.ReadAllText(file);
        var classes = Regex.Matches(text, @"^public\s+(?:static\s+)?class\s+(\w+)", RegexOptions.Multiline);
        var attrs = Regex.Matches(text, @"\[McpServerTool\b(?!Type)");
        var ends = Regex.Matches(text, @"\[McpServerToolType\]").Select(m => m.Index)
            .Concat(attrs.Select(m => m.Index)).Append(text.Length).Order().ToList();
        foreach (Match a in attrs)
        {
            var end = ends.First(e => e > a.Index);
            var segment = text.Substring(a.Index, end - a.Index);
            var m = Regex.Match(segment, @"^\s*public\s+[^\r\n=]*?\b(\w+)\s*(?:<[^>\r\n]*>)?\(", RegexOptions.Multiline);
            if (!m.Success) throw new InvalidOperationException($"Не найдена сигнатура метода после [McpServerTool] в {file}");
            var cls = classes.Cast<Match>().Last(c => c.Index < a.Index).Groups[1].Value;
            result[cls + "." + m.Groups[1].Value] = Regex.IsMatch(segment, @"ToolRunner\.RunWrite(Async)?\(");
        }
    }
    return result;
}

static string TypeName(Type t)
{
    if (Nullable.GetUnderlyingType(t) is { } u) return TypeName(u) + "?";
    if (t.IsArray) return TypeName(t.GetElementType()!) + "[]";
    if (t.IsGenericType)
        return t.Name[..t.Name.IndexOf('`')] + "<" + string.Join(", ", t.GetGenericArguments().Select(TypeName)) + ">";
    return t == typeof(string) ? "string" : t == typeof(int) ? "int" : t == typeof(long) ? "long"
        : t == typeof(bool) ? "bool" : t == typeof(double) ? "double" : t == typeof(float) ? "float"
        : t == typeof(decimal) ? "decimal" : t == typeof(object) ? "object" : t.Name;
}

static string DefaultText(object? v) => v switch
{
    null => "null",
    string s => "\"" + s + "\"",
    bool b => b ? "true" : "false",
    IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
    _ => v.ToString() ?? "null"
};

// Первое предложение описания, не длиннее 160 символов
static string Short(string d)
{
    var m = Regex.Match(d, @"^.*?(?:\.(?=\s)|$)", RegexOptions.Singleline);
    var s = m.Value.Trim();
    return s.Length > 160 ? s[..159].TrimEnd() + "…" : s;
}

static string Esc(string s) =>
    Regex.Replace(s.Replace("\r", "").Replace("\n", " "), @"([\\`*_<>|\[\]])", @"\$1");

sealed record Param(string Name, string Type, bool Required, string Default, string Description);
sealed record Tool(string Name, string Class, string Slug, string Title, string Description, List<Param> Params, bool IsWrite);
