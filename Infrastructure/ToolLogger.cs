using System.Text;

namespace PilotMCP;

/// <summary>
/// Логирует все вызовы MCP-инструментов в файл для отладки.
/// Лог пишется в %TEMP%/pilot-mcp/tools.log
/// </summary>
public static class ToolLogger
{
    private static readonly string LogDir = Path.Combine(Path.GetTempPath(), "pilot-mcp");
    private static readonly string LogPath = Path.Combine(LogDir, "tools.log");
    private static readonly object Lock = new();

    static ToolLogger()
    {
        Directory.CreateDirectory(LogDir);
    }

    public static void Log(string toolName, string args, string result)
    {
        var entry = new StringBuilder();
        entry.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {toolName}");
        entry.AppendLine($"  Args: {args}");
        // Truncate long results for readability
        var resultPreview = result.Length > 500 ? result[..500] + "..." : result;
        entry.AppendLine($"  Result: {resultPreview}");
        entry.AppendLine();

        // Логирование — вспомогательная функция: сбой записи (общий файл у нескольких процессов,
        // нет прав, диск) не должен превращать уже выполненный вызов инструмента в ошибку.
        try
        {
            lock (Lock)
            {
                // FileShare.ReadWrite: лог общий для всех процессов PilotMCP.
                using var fs = new FileStream(LogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                var bytes = Encoding.UTF8.GetBytes(entry.ToString());
                fs.Write(bytes, 0, bytes.Length);
            }

            // Also write to stderr for MCP client visibility
            Console.Error.WriteLine($"[PilotMCP] {toolName}({args}) → {(result.Length > 200 ? result[..200] + "..." : result)}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            Console.Error.WriteLine($"[PilotMCP] ToolLogger: не удалось записать лог ({ex.GetType().Name}: {ex.Message})");
        }
    }

    public static void LogError(string toolName, string args, Exception ex)
    {
        Log(toolName, args, $"ERROR: {ex.Message}\n{ex.StackTrace}");
    }
}
