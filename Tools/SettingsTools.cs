using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text;

namespace PilotMCP;

[McpServerToolType]
public class SettingsTools
{
    private readonly PilotConnection _connection;
    public SettingsTools(PilotConnection connection) => _connection = connection;

    [McpServerTool, Description(
        "Получить персональные настройки текущего пользователя. " +
        "API не поддерживает чтение настроек другого пользователя. " +
        "Для изменения настроек любого пользователя используйте ChangeSettings с personId.")]
    public Task<string> GetPersonalSettings(
        [Description("Ключ настройки (пусто = все настройки)")] string key = "")
    {
        return ToolRunner.RunAsync(_connection, "GetPersonalSettings", $"key={key}", async () =>
        {
            var settings = await _connection.ServerApi.GetPersonalSettingsAsync(string.IsNullOrEmpty(key) ? null! : key);

            var sb = new StringBuilder();
            sb.AppendLine("Персональные настройки:");
            if (settings?.PersonalSettings != null)
            {
                foreach (var kv in settings.PersonalSettings)
                {
                    sb.AppendLine($"  [{kv.Key}]:");
                    if (kv.Value?.Values != null)
                        foreach (var inner in kv.Value.Values)
                            sb.AppendLine($"    {inner.Key} = {(inner.Value?.Length > 200 ? inner.Value[..200] + "..." : inner.Value)}");
                }
            }
            if (settings?.CommonSettings != null)
            {
                sb.AppendLine("Общие настройки:");
                foreach (var kv in settings.CommonSettings)
                {
                    sb.AppendLine($"  [{kv.Key}]:");
                    if (kv.Value?.Values != null)
                        foreach (var inner in kv.Value.Values)
                            sb.AppendLine($"    {inner.Key} = {(inner.Value?.Length > 200 ? inner.Value[..200] + "..." : inner.Value)}");
                }
            }

            return ToolResult.Ok(sb.Length > 0 ? sb.ToString() : "Настройки пусты.");
        });
    }

    [McpServerTool, Description(
        "Получить общие настройки базы данных.")]
    public Task<string> GetCommonSettings()
    {
        return ToolRunner.RunAsync(_connection, "GetCommonSettings", "", async () =>
        {
            var settings = await _connection.ServerApi.GetCommonSettingsAsync();

            var sb = new StringBuilder();
            sb.AppendLine("Общие настройки базы:");
            if (settings?.CommonSettings != null)
            {
                foreach (var kv in settings.CommonSettings)
                {
                    sb.AppendLine($"  [{kv.Key}]:");
                    if (kv.Value?.Values != null)
                        foreach (var inner in kv.Value.Values)
                            sb.AppendLine($"    {inner.Key} = {(inner.Value?.Length > 200 ? inner.Value[..200] + "..." : inner.Value)}");
                }
            }

            return ToolResult.Ok(sb.Length > 0 ? sb.ToString() : "Настройки пусты.");
        });
    }
}

[McpServerToolType]
public class CommandTools
{
    private readonly PilotConnection _connection;
    public CommandTools(PilotConnection connection) => _connection = connection;

    [McpServerTool, Description(
        "Вызвать команду, зарегистрированную серверным расширением Pilot Server (плагином на стороне сервера). " +
        "Работает ТОЛЬКО с командами расширений, имя которых известно заранее. " +
        "НЕ вызывает MCP-инструменты, методы SDK или внутренние функции — если нужного действия нет в списке инструментов, " +
        "InvokeCommand его не заменит, и подбирать имена команд бесполезно.")]
    public Task<string> InvokeCommand(
        [Description("Имя команды серверного расширения")] string commandName,
        [Description("Данные команды в формате base64 (пусто, если не нужны)")] string data = "")
    {
        return ToolRunner.RunWriteAsync(_connection, "InvokeCommand", $"cmd={commandName}, dataLen={data?.Length ?? 0}", async () =>
        {
            byte[] bytes;
            try
            {
                bytes = string.IsNullOrEmpty(data) ? Array.Empty<byte>() : Convert.FromBase64String(data);
            }
            catch (FormatException)
            {
                return ToolResult.Error(
                    "Параметр data не является корректной base64-строкой. " +
                    "Передайте данные в base64 или пустую строку. Запрос до сервера не дошёл.");
            }

            var requestId = Guid.NewGuid();
            try
            {
                await _connection.ServerApi.InvokeCommandAsync(commandName, requestId, bytes);
            }
            catch (Exception ex) when (ex.Message.Contains("handler is not registered"))
            {
                return ToolResult.Error(
                    $"Команда '{commandName}' не зарегистрирована на сервере Pilot. " +
                    "InvokeCommand вызывает только команды серверных расширений; перебирать варианты имени бесполезно — " +
                    "если команда не зарегистрирована, её не существует. Используйте инструменты из списка MCP.");
            }

            return ToolResult.Ok($"Команда '{commandName}' отправлена. RequestId: {requestId}");
        });
    }
}
