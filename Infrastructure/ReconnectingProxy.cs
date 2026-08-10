using System.Reflection;

namespace PilotMCP;

/// <summary>
/// DispatchProxy-обёртка для API-интерфейсов Pilot.
/// При ошибке соединения (Transport closed, disposed и т.д.) автоматически
/// переподключается и повторяет вызов.
/// Поддерживает как sync, так и async (Task-returning) методы.
/// </summary>
public class ReconnectingProxy<T> : DispatchProxy where T : class
{
    private Func<T> _targetFactory = null!;
    private Action _restore = null!;
    private Func<bool> _isConnectionLost = null!;
    private T _target = null!;

    public static T Create(T initialTarget, Func<T> targetFactory, Action restore, Func<bool> isConnectionLost)
    {
        var proxy = Create<T, ReconnectingProxy<T>>() as ReconnectingProxy<T>
            ?? throw new InvalidOperationException("Failed to create proxy");
        proxy._target = initialTarget;
        proxy._targetFactory = targetFactory;
        proxy._restore = restore;
        proxy._isConnectionLost = isConnectionLost;
        return (proxy as T)!;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod == null) throw new ArgumentNullException(nameof(targetMethod));

        // Если соединение уже потеряно — восстановить ДО вызова
        if (_isConnectionLost())
            Restore(targetMethod.Name, "ConnectionLost flag");

        try
        {
            var result = targetMethod.Invoke(_target, args);

            // Для async методов (возвращающих Task) — перехватываем ошибку внутри Task
            if (result is Task task)
                return WrapTask(task, targetMethod, args);

            return result;
        }
        catch (TargetInvocationException tie) when (IsRecoverable(tie.InnerException))
        {
            return Retry(targetMethod, args, tie.InnerException!);
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            return Retry(targetMethod, args, ex);
        }
    }

    /// <summary>
    /// Оборачивает Task — если он завершится с recoverable ошибкой,
    /// делает reconnect и повторяет вызов.
    /// </summary>
    private object WrapTask(Task originalTask, MethodInfo method, object?[]? args)
    {
        var returnType = method.ReturnType;

        // Task<T>
        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
        {
            var resultType = returnType.GetGenericArguments()[0];
            var wrapMethod = typeof(ReconnectingProxy<T>)
                .GetMethod(nameof(WrapTaskGeneric), BindingFlags.NonPublic | BindingFlags.Instance)!
                .MakeGenericMethod(resultType);
            return wrapMethod.Invoke(this, new object[] { originalTask, method, args! })!;
        }

        // Task (void)
        return WrapTaskVoid(originalTask, method, args);
    }

    private async Task WrapTaskVoid(Task originalTask, MethodInfo method, object?[]? args)
    {
        try
        {
            await originalTask;
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            Restore(method.Name, ex.GetType().Name);
            var retryResult = method.Invoke(_target, args);
            if (retryResult is Task retryTask)
                await retryTask;
        }
    }

    private async Task<TResult> WrapTaskGeneric<TResult>(Task<TResult> originalTask, MethodInfo method, object?[]? args)
    {
        try
        {
            return await originalTask;
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            Restore(method.Name, ex.GetType().Name);
            var retryResult = method.Invoke(_target, args);
            if (retryResult is Task<TResult> retryTask)
                return await retryTask;
            throw;
        }
    }

    /// <summary>
    /// Переподключается и обновляет target.
    /// DoReconnect() на уровне PilotConnection сам обрабатывает повторные вызовы
    /// (lock + re-entrant guard), поэтому здесь guard не нужен.
    /// </summary>
    private void Restore(string methodName, string reason)
    {
        Console.Error.WriteLine($"[PilotMCP] {typeof(T).Name}.{methodName}: {reason} — переподключение...");
        _restore();
        _target = _targetFactory();
        Console.Error.WriteLine($"[PilotMCP] {typeof(T).Name}: target обновлён.");
    }

    private object? Retry(MethodInfo method, object?[]? args, Exception original)
    {
        Restore(method.Name, original.GetType().Name);
        try
        {
            return method.Invoke(_target, args);
        }
        catch (TargetInvocationException tie)
        {
            throw tie.InnerException ?? tie;
        }
    }

    /// <summary>
    /// Определяет, является ли ошибка транспортной (соединение потеряно/закрыто) —
    /// используется также PilotConnection для админ-подключения.
    /// </summary>
    internal static bool IsRecoverableError(Exception? ex) => IsRecoverable(ex);

    private static bool IsRecoverable(Exception? ex)
    {
        if (ex == null) return false;
        if (ex is ObjectDisposedException) return true;
        if (ex is TimeoutException) return true;
        var msg = ex.Message;
        if (msg != null && (
            msg.Contains("disposed", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("Transport closed", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("TransportClient", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("transport", StringComparison.OrdinalIgnoreCase)))
            return true;
        if (ex.InnerException != null) return IsRecoverable(ex.InnerException);
        return false;
    }
}
