using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PilotMCP.Tests;

/// <summary>
/// MCP клиент для интеграционных тестов.
/// Один процесс PilotMCP.exe на весь прогон, без перезапусков.
/// </summary>
public class McpTestClient : IAsyncDisposable
{
    private Process? _process;
    private StreamWriter? _stdin;
    private StreamReader? _stdout;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Queue<string> _stderrLines = new();
    private int _requestId;
    private bool _initialized;

    /// <summary>Последние строки stderr процесса PilotMCP — для диагностики упавших тестов.</summary>
    public string[] GetStderrTail()
    {
        lock (_stderrLines) return _stderrLines.ToArray();
    }

    private McpTestClient() { }

    public static async Task<McpTestClient> CreateAsync(int timeoutMs = 15000)
    {
        var client = new McpTestClient();

        var exePath = FindExePath();
        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardInputEncoding = Encoding.UTF8,
        };

        var envVars = new[] { "PILOT_SERVER_URL", "PILOT_LOGIN", "PILOT_PASSWORD", "PILOT_DATABASE", "PILOT_LICENSE_TYPE", "PILOT_READONLY", "PILOT_ADMIN_LOGIN", "PILOT_ADMIN_PASSWORD" };
        foreach (var key in envVars)
        {
            var val = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrEmpty(val))
                psi.Environment[key] = val;
        }

        client._process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start PilotMCP process");
        client._stdin = client._process.StandardInput;
        client._stdin.AutoFlush = true;
        client._stdout = new StreamReader(client._process.StandardOutput.BaseStream, Encoding.UTF8, false, 256 * 1024);

        // stderr обязательно вычитывать: иначе при заполнении буфера пайпа (~4КБ)
        // процесс PilotMCP блокируется на Console.Error.WriteLine и все вызовы виснут.
        client._process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            lock (client._stderrLines)
            {
                client._stderrLines.Enqueue(e.Data);
                while (client._stderrLines.Count > 200) client._stderrLines.Dequeue();
            }
        };
        client._process.BeginErrorReadLine();

        // Initialize MCP
        var initResponse = await client.SendRequestInternalAsync("initialize", new JsonObject
        {
            ["protocolVersion"] = "2024-11-05",
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "integration-test", ["version"] = "1.0" }
        }, timeoutMs);

        await client._stdin.WriteLineAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "notifications/initialized"
        }.ToJsonString());

        await Task.Delay(1000);
        client._initialized = true;
        return client;
    }

    public async Task<ToolCallResult> CallToolAsync(string toolName, JsonObject? arguments = null, int timeoutMs = 30000)
    {
        if (!_initialized)
            throw new InvalidOperationException("Client not initialized");

        await _lock.WaitAsync();
        try
        {
            var args = arguments ?? new JsonObject();
            var response = await SendRequestInternalAsync("tools/call", new JsonObject
            {
                ["name"] = toolName,
                ["arguments"] = args
            }, timeoutMs);

            var result = response["result"]?.AsObject();
            var isError = result?["isError"]?.GetValue<bool>() ?? false;
            var content = result?["content"]?.AsArray();
            var text = content?.FirstOrDefault()?["text"]?.GetValue<string>() ?? "";

            return new ToolCallResult
            {
                IsError = isError,
                RawText = text,
                Json = TryParseJson(text)
            };
        }
        catch (TimeoutException)
        {
            throw;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<JsonObject> SendRequestInternalAsync(string method, JsonObject @params, int timeoutMs)
    {
        var id = Interlocked.Increment(ref _requestId);
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = @params
        };

        await _stdin!.WriteLineAsync(request.ToJsonString());

        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var remaining = Math.Max((int)(deadline - DateTime.UtcNow).TotalMilliseconds, 1);
            var readTask = Task.Run(() => _stdout!.ReadLine());

            if (!readTask.Wait(remaining))
                throw new TimeoutException($"Timeout ({timeoutMs}ms) waiting for {method} id={id}");

            var line = readTask.Result;
            if (string.IsNullOrWhiteSpace(line)) continue;

            var json = JsonNode.Parse(line)?.AsObject();
            if (json == null) continue;

            if (json.ContainsKey("id") && json["id"]?.GetValue<int>() == id)
                return json;
        }

        throw new TimeoutException($"Timeout ({timeoutMs}ms) waiting for {method} id={id}");
    }

    private static JsonObject? TryParseJson(string text)
    {
        try { return JsonNode.Parse(text)?.AsObject(); }
        catch { return null; }
    }

    private static string FindExePath()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "bin", "Release", "net8.0", "PilotMCP.exe"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "bin", "Debug", "net8.0", "PilotMCP.exe"),
        };

        // Из существующих сборок берём самую свежую — стабильный порядок (Release-first)
        // приводил к прогону тестов на устаревшем exe.
        var newest = candidates
            .Select(Path.GetFullPath)
            .Where(File.Exists)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        if (newest != null) return newest;

        var envPath = Environment.GetEnvironmentVariable("PILOTMCP_EXE");
        if (!string.IsNullOrEmpty(envPath) && File.Exists(envPath))
            return envPath;

        throw new FileNotFoundException(
            "PilotMCP.exe not found. Build the project first (dotnet build -c Release) " +
            "or set PILOTMCP_EXE environment variable.");
    }

    public async ValueTask DisposeAsync()
    {
        try { _stdin?.Close(); } catch { }
        try
        {
            if (_process != null && !_process.HasExited)
            {
                _process.Kill();
                _process.WaitForExit(3000);
            }
        }
        catch { }
        _process?.Dispose();
        _lock.Dispose();
    }
}

public class ToolCallResult
{
    public bool IsError { get; set; }
    public string RawText { get; set; } = "";
    public JsonObject? Json { get; set; }

    public string Status => Json?["status"]?.GetValue<string>() ?? (IsError ? "error" : "unknown");
    public string Message => Json?["message"]?.GetValue<string>() ?? RawText;
    public string? ResultFile => Json?["resultFile"]?.GetValue<string>();
    public int Count => Json?["count"]?.GetValue<int>() ?? 0;

    public void AssertOk()
    {
        Assert.False(IsError, $"Tool call returned error: {RawText}");
        if (Json != null)
            Assert.Equal("ok", Status);
    }

    public void AssertError()
    {
        Assert.True(IsError || Status == "error", $"Expected error but got: {RawText}");
    }
}
