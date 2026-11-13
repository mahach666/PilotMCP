using Ascon.Pilot.Transport;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.ExceptionServices;

namespace PilotMCP;

/// <summary>
/// DispatchProxy-обёртка для API-интерфейсов Pilot.
/// При ошибке соединения (Transport closed, disposed и т.д.) автоматически
/// переподключается. Вызов повторяется только если это безопасно: метод читающий
/// (Get*/Load*/Calc*/Open*...) либо цель уже была disposed (запрос не уходил на сервер).
/// Записи после таймаута/обрыва не повторяются — сервер мог уже применить изменение.
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
            var result = InvokeTarget(targetMethod, args);

            // Для async методов (возвращающих Task) — перехватываем ошибку внутри Task
            if (result is Task task)
                return WrapTask(task, targetMethod, args);

            return result;
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
            if (!CanReplay(method, ex)) throw;
            var retryResult = InvokeTarget(method, args);
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
            if (!CanReplay(method, ex)) throw;
            var retryResult = InvokeTarget(method, args);
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
        if (!CanReplay(method, original)) Rethrow(original);
        return InvokeTarget(method, args);
    }

    /// <summary>Вызов метода target с разворачиванием TargetInvocationException — наружу идёт настоящая ошибка сервера.</summary>
    private object? InvokeTarget(MethodInfo method, object?[]? args)
    {
        try
        {
            return method.Invoke(_target, args);
        }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            Rethrow(tie.InnerException);
            return null;
        }
    }

    [DoesNotReturn]
    private static void Rethrow(Exception ex) => ExceptionDispatchInfo.Capture(ex).Throw();

    private static readonly string[] ReadOnlyPrefixes = { "Get", "Load", "Calc", "Open", "Check", "Enumerate", "SearchMessages" };

    /// <summary>
    /// Можно ли безопасно повторить вызов после reconnect: читающие методы — всегда;
    /// остальные — только если цель была disposed (запрос на сервер не уходил).
    /// После таймаута/обрыва записи (ChangeAsync, SendMessage, InvokeCommand...) сервер мог уже
    /// выполнить вызов, повтор дал бы дубликат.
    /// </summary>
    private static bool CanReplay(MethodInfo method, Exception ex)
    {
        if (ReadOnlyPrefixes.Any(p => method.Name.StartsWith(p, StringComparison.Ordinal))) return true;
        for (var e = ex; e != null; e = e.InnerException)
            if (e is ObjectDisposedException) return true;
        return false;
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
        if (ex is TimeoutException or TransportException) return true;
        var msg = ex.Message;
        if (msg != null && (
            msg.Contains("disposed", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("Transport closed", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("TransportClient", StringComparison.OrdinalIgnoreCase)))
            return true;
        if (ex.InnerException != null) return IsRecoverable(ex.InnerException);
        return false;
    }
}
