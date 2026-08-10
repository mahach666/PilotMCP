namespace PilotMCP.Tests;

/// <summary>
/// Shared fixture — один процесс PilotMCP на всю коллекцию тестов.
/// Тесты внутри коллекции выполняются последовательно (один MCP-процесс).
/// </summary>
public class PilotFixture : IAsyncLifetime
{
    public McpTestClient Client { get; private set; } = null!;

    /// <summary>
    /// Задан ли явно администратор сервера. Если задан — админ-инструменты обязаны работать,
    /// и ошибка от них считается регрессией, а не отсутствием прав.
    /// </summary>
    public static bool AdminExpected =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PILOT_ADMIN_LOGIN"));

    public async Task InitializeAsync()
    {
        Client = await McpTestClient.CreateAsync(15000);
    }

    public async Task DisposeAsync()
    {
        await Client.DisposeAsync();
    }
}

[CollectionDefinition("Pilot")]
public class PilotCollection : ICollectionFixture<PilotFixture> { }

/// <summary>
/// Помощники для админ-тестов: без учётки администратора сервера они пропускаются,
/// но при заданном PILOT_ADMIN_LOGIN пропуск запрещён — иначе реальная регрессия
/// (сломанный CreatePerson, CreateType и т.д.) молча зеленит всю пачку тестов.
/// </summary>
internal static class AdminGate
{
    /// <summary>
    /// Пропустить тест, если админ-доступа нет. Возвращает true — тест надо прервать.
    /// При заданном PILOT_ADMIN_LOGIN вместо пропуска падает.
    /// </summary>
    public static bool ShouldSkip(ToolCallResult result, string tag)
    {
        if (result.Status != "error")
            return false;

        Assert.False(PilotFixture.AdminExpected,
            $"[{tag}] админ-операция вернула ошибку, хотя PILOT_ADMIN_LOGIN задан: {result.Message}");

        Console.WriteLine($"[{tag}] SKIP (нет админ-доступа): {result.Message}");
        return true;
    }

    /// <summary>
    /// Проверяет, что инструмент вернул ошибку именно по ожидаемой причине.
    /// Без админ-доступа причина может быть другой (нет прав) — тогда проверяется только факт ошибки.
    /// </summary>
    public static void AssertErrorBecause(ToolCallResult result, string expectedFragment)
    {
        Assert.Equal("error", result.Status);
        if (PilotFixture.AdminExpected)
            Assert.Contains(expectedFragment, result.Message, StringComparison.OrdinalIgnoreCase);
    }
}
