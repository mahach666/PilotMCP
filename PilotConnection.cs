using Ascon.Pilot.Common.DataProtection;
using Ascon.Pilot.DataClasses;
using Ascon.Pilot.DataModifier;
using Ascon.Pilot.Server.Api;
using Ascon.Pilot.Server.Api.Contracts;
using Ascon.Pilot.Transport;
using System.Collections.Concurrent;
using IConnectionLostListener = Ascon.Pilot.Transport.IConnectionLostListener;

namespace PilotMCP;

public class PilotConnection : IDisposable
{
    private HttpPilotClient? _client;
    private IServerAsyncApi? _rawServerApi;
    private IServerApi? _rawSyncServerApi; // для Backend/Modifier
    private IAuthenticationApi? _authApi;
    private IFileArchiveApi? _rawFileApi;
    private IMessagesApi? _rawMessagesApi;
    private DMetadata? _metadata;
    private DDatabaseInfo? _dbInfo;
    private string? _databaseName;
    private ServerCallback? _callback;
    private MessageCallback? _messageCallback;
    private volatile bool _connectionLost;
    private readonly object _reconnectLock = new();
    private volatile bool _reconnectInProgress;
    private volatile bool _metadataStale; // сервер сообщил об изменении метаданных — перечитать при следующем обращении
    private readonly SemaphoreSlim _metadataLock = new(1, 1); // сериализует read-modify-write метаданных
    private readonly List<string> _lastErrors = new();
    private Backend? _backend;
    private FileSystemStorageProvider? _storageProvider;

    // Прокси с автопереподключением
    private IServerApi? _proxySyncServerApi;
    private IServerAsyncApi? _proxyServerApi;
    private IFileArchiveApi? _proxyFileApi;
    private IMessagesApi? _proxyMessagesApi;

    // Админ-подключение (LoginServerAdministrator) — создаётся лениво при первом обращении
    private HttpPilotClient? _adminClient;
    private IServerAdminAsyncApi? _adminApi;
    private volatile bool _adminConnectionLost;
    private readonly object _adminLock = new();

    // Сохранённые креденшалы для автопереподключения
    private string? _savedServerUrl;
    private string? _savedLogin;
    private string? _savedPassword;
    private string? _savedDatabase;
    private int _savedLicenseType = 100;

    public bool IsConnected => _client != null && _rawServerApi != null;

    private string NotConnectedMessage
    {
        get
        {
            var msg = "Не подключено к серверу Pilot.";
            lock (_lastErrors)
            {
                if (_lastErrors.Count > 0)
                    msg += "\nПоследние ошибки:\n- " + string.Join("\n- ", _lastErrors);
            }
            return msg;
        }
    }
    public bool IsReadOnly { get; set; }

    public string? ReadOnlyError => IsReadOnly
        ? "РЕЖИМ ТОЛЬКО ЧТЕНИЯ АКТИВЕН: эта операция недоступна. Сообщите пользователю, что сервер работает в режиме только чтения (PILOT_READONLY=true). Для выполнения операций записи необходимо отключить PILOT_READONLY."
        : null;

    /// <summary>
    /// ServerApi с автоматическим переподключением при TransportClient disposed.
    /// </summary>
    public IServerAsyncApi ServerApi => _proxyServerApi ?? throw new InvalidOperationException(NotConnectedMessage);

    /// <summary>
    /// FileApi с автоматическим переподключением.
    /// </summary>
    public IFileArchiveApi FileApi => _proxyFileApi ?? throw new InvalidOperationException(NotConnectedMessage);

    /// <summary>
    /// MessagesApi с автоматическим переподключением.
    /// </summary>
    public IMessagesApi? MessagesApi => _proxyMessagesApi;

    public DMetadata Metadata => _metadata ?? throw new InvalidOperationException("Метаданные не загружены.");

    /// <summary>
    /// Создаёт Modifier из DataModifier SDK для высокоуровневых операций (удаление, перемещение и т.д.).
    /// MergeChangePolicy.Allow — не отклонять при конкурентных изменениях.
    /// </summary>
    public IModifier CreateModifier()
    {
        if (_backend == null)
            throw new InvalidOperationException(NotConnectedMessage);
        return new Modifier(_backend, ChangesetDataSource.Native, MergeChangePolicy.Allow);
    }

    public IFileStorageProvider StorageProvider => _storageProvider
        ?? throw new InvalidOperationException("Не подключено к серверу Pilot.");

    /// <summary>Backend для TaskModifier и других высокоуровневых операций.</summary>
    public IBackend Backend => _backend
        ?? throw new InvalidOperationException("Не подключено к серверу Pilot.");
    public DDatabaseInfo DatabaseInfo => _dbInfo ?? throw new InvalidOperationException("База данных не открыта.");
    public string DatabaseName => _databaseName ?? throw new InvalidOperationException("Не подключено.");
    public int CurrentPersonId => _dbInfo?.Person?.Id ?? 0;

    public string Connect(string serverUrl, string login, string password, string? databaseName = null, int licenseType = 100)
    {
        // Тот же lock, что у DoReconnect/EnsureConnected: параллельные логины с одной учёткой выбивают сессии друг друга.
        lock (_reconnectLock)
        {
            return ConnectCore(serverUrl, login, password, databaseName, licenseType);
        }
    }

    private string ConnectCore(string serverUrl, string login, string password, string? databaseName, int licenseType)
    {
        _savedServerUrl = serverUrl;
        _savedLogin = login;
        _savedPassword = password;
        _savedDatabase = databaseName;
        _savedLicenseType = licenseType;

        if (IsConnected)
            Disconnect();

        try
        {
            var url = serverUrl;
            if (!string.IsNullOrWhiteSpace(databaseName))
            {
                var uri = new Uri(serverUrl);
                if (string.IsNullOrWhiteSpace(uri.AbsolutePath) || uri.AbsolutePath == "/")
                    url = serverUrl.TrimEnd('/') + "/" + databaseName;
            }

            Console.Error.WriteLine($"[PilotMCP] Подключение к {url}, логин: {login}");

            var credentials = ConnectionCredentials.GetConnectionCredentials(
                url, login, password.ConvertToSecureString());

            Console.Error.WriteLine($"[PilotMCP] DatabaseName из URL: {credentials.DatabaseName}");

            _client = new HttpPilotClient(credentials.GetConnectionString(), credentials.GetConnectionProxy());
            _client.SetConnectionLostListener(new ConnectionLostHandler(this));
            _connectionLost = false;
            _client.Connect(false);
            Console.Error.WriteLine("[PilotMCP] HTTP клиент подключён");

            _authApi = _client.GetAuthenticationApi();
            _callback = new ServerCallback(() => _metadataStale = true);
            _rawServerApi = _client.GetServerAsyncApi(_callback);
            _rawSyncServerApi = _client.GetServerApi(_callback);
            _rawFileApi = _client.GetFileArchiveApi();
            try
            {
                _messageCallback = new MessageCallback();
                _rawMessagesApi = _client.GetMessagesApi(_messageCallback);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[PilotMCP] MessagesApi недоступен: {ex.Message}");
                _rawMessagesApi = null;
            }

            _databaseName = string.IsNullOrWhiteSpace(databaseName)
                ? credentials.DatabaseName
                : databaseName;

            Console.Error.WriteLine($"[PilotMCP] Авторизация: база={_databaseName}, лицензия={licenseType}");
            _authApi.Login(_databaseName, credentials.Username, credentials.ProtectedPassword, false, licenseType);
            Console.Error.WriteLine("[PilotMCP] Авторизация успешна");

            _dbInfo = Task.Run(() => _rawServerApi.OpenDatabaseAsync()).GetAwaiter().GetResult();
            _metadata = Task.Run(() => _rawServerApi.GetMetadataAsync(_dbInfo?.MetadataVersion ?? 0)).GetAwaiter().GetResult();
            Console.Error.WriteLine($"[PilotMCP] База открыта, типов: {_metadata.Types.Count}");

            // Создаём прокси с автопереподключением
            CreateProxies();

            // Backend + StorageProvider для Modifier (через прокси для авто-реконнекта)
            RebuildBackend();

            return $"Подключено к {url}, база: {_databaseName}, типов объектов: {_metadata.Types.Count}";
        }
        catch (Exception ex)
        {
            Disconnect();
            var msg = $"Ошибка подключения: {ex.GetType().Name}: {ex.Message}";
            Console.Error.WriteLine($"[PilotMCP] {msg}");
            if (ex.InnerException != null)
                Console.Error.WriteLine($"[PilotMCP] Inner: {ex.InnerException.Message}");
            throw new InvalidOperationException(msg, ex);
        }
    }

    private void CreateProxies()
    {
        _proxySyncServerApi = ReconnectingProxy<IServerApi>.Create(
            _rawSyncServerApi!,
            () => _rawSyncServerApi!,
            DoReconnect,
            () => _connectionLost);

        _proxyServerApi = ReconnectingProxy<IServerAsyncApi>.Create(
            _rawServerApi!,
            () => _rawServerApi!,
            DoReconnect,
            () => _connectionLost);

        _proxyFileApi = ReconnectingProxy<IFileArchiveApi>.Create(
            _rawFileApi!,
            () => _rawFileApi!,
            DoReconnect,
            () => _connectionLost);

        if (_rawMessagesApi != null)
        {
            _proxyMessagesApi = ReconnectingProxy<IMessagesApi>.Create(
                _rawMessagesApi,
                () => _rawMessagesApi!,
                DoReconnect,
                () => _connectionLost);
        }
    }

    /// <summary>
    /// Полное переподключение — убиваем старый клиент, создаём новый с нуля.
    /// Потокобезопасно: lock предотвращает одновременные переподключения.
    /// Реентрантно: повторный вызов из того же потока (например, из RebuildBackend → Backend → proxy)
    /// возвращает управление без повторного переподключения — raw API уже обновлены.
    /// </summary>
    private void DoReconnect()
    {
        lock (_reconnectLock)
        {
            // Реентрантный вызов (тот же поток) — соединение уже пересоздаётся,
            // raw API уже обновлены, просто выходим чтобы proxy обновил свой target.
            if (_reconnectInProgress)
            {
                Console.Error.WriteLine("[PilotMCP] DoReconnect: реентрантный вызов, пропуск");
                return;
            }

            // Другой поток/proxy уже переподключился — соединение живое
            if (!_connectionLost && _client != null && _rawServerApi != null)
            {
                Console.Error.WriteLine("[PilotMCP] DoReconnect: соединение живое, пропуск");
                return;
            }

            _reconnectInProgress = true;
            try
            {
                DoReconnectWithRetry();
            }
            finally
            {
                _reconnectInProgress = false;
            }
        }
    }

    /// <summary>
    /// Выполняет переподключение с повторными попытками (до 3 раз с нарастающей задержкой).
    /// </summary>
    private void DoReconnectWithRetry()
    {
        const int maxAttempts = 3;
        Exception? lastError = null;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                DoReconnectCore();
                lock (_lastErrors) { _lastErrors.Clear(); }
                return;
            }
            catch (Exception ex)
            {
                lastError = ex;
                var msg = $"Попытка {attempt}/{maxAttempts}: {ex.GetType().Name}: {ex.Message}";
                Console.Error.WriteLine($"[PilotMCP] DoReconnect: {msg}");
                lock (_lastErrors)
                {
                    _lastErrors.Add(msg);
                    while (_lastErrors.Count > 10) _lastErrors.RemoveAt(0);
                }

                if (attempt < maxAttempts)
                    Thread.Sleep(attempt * 1000);
            }
        }

        // Все попытки провалились: не оставляем полуинициализированный клиент (без логина/БД),
        // который IsConnected считал бы живым. После сброса EnsureConnected сделает полный Connect.
        TearDownClient();
        _connectionLost = true;

        throw new InvalidOperationException(
            $"Не удалось переподключиться после {maxAttempts} попыток: {lastError!.Message}", lastError);
    }

    /// <summary>
    /// Сбрасывает клиент и все производные raw API (после этого IsConnected == false).
    /// </summary>
    private void TearDownClient()
    {
        var oldClient = _client;
        _client = null;
        _rawServerApi = null;
        _rawSyncServerApi = null;
        _authApi = null;
        _rawFileApi = null;
        _rawMessagesApi = null;
        _backend = null;
        _callback = null;
        _messageCallback = null;

        if (oldClient != null)
        {
            var disconnectTask = Task.Run(() =>
            {
                try { oldClient.Disconnect(); } catch { }
                try { (oldClient as IDisposable)?.Dispose(); } catch { }
            });
            disconnectTask.Wait(5000); // Ждём макс 5 секунд
            Console.Error.WriteLine($"[PilotMCP] Старый клиент отключён: {(disconnectTask.IsCompleted ? "ok" : "timeout")}");
        }
    }

    /// <summary>
    /// Ядро переподключения — один атомарный цикл: disconnect → connect → login → open.
    /// </summary>
    private void DoReconnectCore()
    {
        _connectionLost = false;

        Console.Error.WriteLine("[PilotMCP] Полное переподключение...");

        TearDownClient();

        if (string.IsNullOrEmpty(_savedServerUrl) || string.IsNullOrEmpty(_savedLogin))
            throw new InvalidOperationException("Нет сохранённых креденшалов для переподключения.");

        var url = _savedServerUrl!;
        if (!string.IsNullOrWhiteSpace(_savedDatabase))
        {
            var uri = new Uri(_savedServerUrl!);
            if (string.IsNullOrWhiteSpace(uri.AbsolutePath) || uri.AbsolutePath == "/")
                url = _savedServerUrl!.TrimEnd('/') + "/" + _savedDatabase;
        }

        var credentials = ConnectionCredentials.GetConnectionCredentials(
            url, _savedLogin!, (_savedPassword ?? "").ConvertToSecureString());

        _client = new HttpPilotClient(credentials.GetConnectionString(), credentials.GetConnectionProxy());
        _client.SetConnectionLostListener(new ConnectionLostHandler(this));
        _connectionLost = false;
        _client.Connect(false);
        Console.Error.WriteLine("[PilotMCP] DoReconnect: HTTP клиент создан");

        _authApi = _client.GetAuthenticationApi();
        _callback = new ServerCallback(() => _metadataStale = true);
        _rawServerApi = _client.GetServerAsyncApi(_callback);
        _rawSyncServerApi = _client.GetServerApi(_callback);
        _rawFileApi = _client.GetFileArchiveApi();
        try
        {
            _messageCallback = new MessageCallback();
            _rawMessagesApi = _client.GetMessagesApi(_messageCallback);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[PilotMCP] DoReconnect: MessagesApi недоступен: {ex.Message}");
            _rawMessagesApi = null;
        }

        _authApi.Login(_databaseName!, credentials.Username, credentials.ProtectedPassword, false, _savedLicenseType);
        Console.Error.WriteLine("[PilotMCP] DoReconnect: авторизация успешна");

        _dbInfo = Task.Run(() => _rawServerApi.OpenDatabaseAsync()).GetAwaiter().GetResult();
        _metadata = Task.Run(() => _rawServerApi.GetMetadataAsync(_dbInfo?.MetadataVersion ?? 0)).GetAwaiter().GetResult();
        Console.Error.WriteLine($"[PilotMCP] DoReconnect: база открыта, типов: {_metadata.Types.Count}");

        RebuildBackend();
        Console.Error.WriteLine("[PilotMCP] Полное переподключение успешно.");
    }

    private void RebuildBackend()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "pilot-mcp", "storage");
        Directory.CreateDirectory(tempDir);
        _storageProvider = new FileSystemStorageProvider(tempDir);
        var changesetUploader = new ChangesetUploader(_rawFileApi!, _storageProvider, null);
        // Используем прокси для sync API — чтобы Backend автоматически переподключался
        _backend = new Backend(_proxySyncServerApi ?? _rawSyncServerApi!, _rawMessagesApi, changesetUploader);
    }

    /// <summary>
    /// Выполняет действие через админский API (IServerAdminAsyncApi).
    /// Подключение создаётся лениво: отдельный HttpPilotClient + LoginServerAdministrator.
    /// Креденшалы: PILOT_ADMIN_LOGIN / PILOT_ADMIN_PASSWORD, при их отсутствии — логин/пароль основного подключения.
    /// При транспортной ошибке админ-клиент сбрасывается (следующий вызов подключится заново),
    /// но сам вызов НЕ повторяется: через этот метод идут неидемпотентные записи
    /// (ChangePeople, UpdateMetadata, MoveOrganisationUnits), и при обрыве в середине вызова
    /// неизвестно, применил ли сервер изменение — молчаливый повтор применил бы его дважды.
    /// </summary>
    public async Task<T> WithAdminApiAsync<T>(Func<IServerAdminAsyncApi, Task<T>> action)
    {
        var api = GetOrCreateAdminApi();
        try
        {
            return await action(api);
        }
        catch (Exception ex) when (ReconnectingProxy<IServerAdminAsyncApi>.IsRecoverableError(ex))
        {
            Console.Error.WriteLine($"[PilotMCP] AdminApi: {ex.GetType().Name}: {ex.Message} — админ-подключение сброшено.");
            ResetAdminClient();
            throw;
        }
    }

    /// <summary>
    /// Атомарно обновляет метаданные базы: читает свежую копию через админский API,
    /// применяет mutate и записывает обратно. Возвращает новую версию метаданных.
    /// После успешной записи локальный кеш метаданных обновляется.
    /// </summary>
    public async Task<long> UpdateMetadataViaAdminAsync(Action<DMetadata> mutate)
    {
        EnsureConnected();
        // Read-modify-write всего объекта метаданных: без сериализации параллельные вызовы
        // (CreateType + AddTypeAttribute) читают одну версию и затирают изменения друг друга.
        // Между процессами PilotMCP это не защищает.
        await _metadataLock.WaitAsync();
        try
        {
            return await WithAdminApiAsync(async admin =>
            {
                var meta = await admin.GetMetadataAsync(DatabaseName, 0);
                mutate(meta);
                var newVersion = await admin.UpdateMetadataAsync(DatabaseName, meta);
                meta.Version = newVersion;
                _metadata = meta;
                _metadataStale = true; // Backend SDK кеширует типы — пересоздать при следующем обращении
                return newVersion;
            });
        }
        finally
        {
            _metadataLock.Release();
        }
    }

    private IServerAdminAsyncApi GetOrCreateAdminApi()
    {
        lock (_adminLock)
        {
            if (_adminApi != null && !_adminConnectionLost)
                return _adminApi;

            DisposeAdminClientLocked();

            var adminLogin = Environment.GetEnvironmentVariable("PILOT_ADMIN_LOGIN");
            var adminPassword = Environment.GetEnvironmentVariable("PILOT_ADMIN_PASSWORD");
            if (string.IsNullOrEmpty(adminLogin))
            {
                adminLogin = _savedLogin;
                adminPassword = _savedPassword;
            }

            if (string.IsNullOrEmpty(_savedServerUrl) || string.IsNullOrEmpty(adminLogin))
                throw new InvalidOperationException(
                    "Нет креденшалов для админ-подключения. Задайте PILOT_ADMIN_LOGIN и PILOT_ADMIN_PASSWORD " +
                    "(или подключитесь к серверу с учёткой администратора сервера).");

            Console.Error.WriteLine($"[PilotMCP] Админ-подключение к {_savedServerUrl}, логин: {adminLogin}");

            var credentials = ConnectionCredentials.GetConnectionCredentials(
                _savedServerUrl, adminLogin, (adminPassword ?? "").ConvertToSecureString());

            _adminConnectionLost = false;
            _adminClient = new HttpPilotClient(credentials.GetConnectionString(), credentials.GetConnectionProxy());
            _adminClient.SetConnectionLostListener(new AdminConnectionLostHandler(this));
            _adminClient.Connect(false);

            var auth = _adminClient.GetAuthenticationApi();
            auth.LoginServerAdministrator(credentials.Username, credentials.ProtectedPassword, false);
            Console.Error.WriteLine("[PilotMCP] Админ-авторизация успешна");

            _adminApi = _adminClient.GetServerAdminAsyncApi(new AdminCallback());
            return _adminApi;
        }
    }

    private void ResetAdminClient()
    {
        lock (_adminLock)
        {
            DisposeAdminClientLocked();
        }
    }

    private void DisposeAdminClientLocked()
    {
        try { _adminClient?.Disconnect(); } catch { }
        try { (_adminClient as IDisposable)?.Dispose(); } catch { }
        _adminClient = null;
        _adminApi = null;
        _adminConnectionLost = false;
    }

    /// <summary>
    /// Выполняет поиск и ждёт результат синхронно (с таймаутом).
    /// </summary>
    public async Task<DSearchResult?> SearchAsync(DSearchDefinition searchDef, int timeoutMs = 30000)
    {
        EnsureConnected();
        var cb = _callback ?? throw new InvalidOperationException(NotConnectedMessage);
        cb.PrepareForSearch(searchDef.Id);
        try
        {
            await ServerApi.AddSearchAsync(searchDef);
            return await cb.WaitForSearchResultAsync(searchDef.Id, timeoutMs);
        }
        finally
        {
            cb.ForgetSearch(searchDef.Id);
        }
    }

    /// <summary>
    /// Применяет изменения (apply — вызов modifier.Apply(...), возвращающий Identity changeset-а)
    /// и ждёт ответа сервера: Backend.Apply только отправляет ChangeAsync, а результат приходит
    /// в NotifyChangeAsyncCompleted/NotifyChangeAsyncError с тем же Identity.
    /// Отказ сервера или отсутствие ответа за timeoutMs → исключение с понятным текстом.
    /// </summary>
    public Guid ApplyAndWait(Func<Guid> apply, int timeoutMs = 60000)
    {
        var id = apply();
        var cb = _callback ?? throw new InvalidOperationException(NotConnectedMessage);
        var tcs = cb.ChangeResult(id);
        try
        {
            if (!tcs.Task.Wait(timeoutMs))
                throw new TimeoutException(
                    $"Сервер не подтвердил изменение за {timeoutMs / 1000} с (changeset {id}). " +
                    "Результат неизвестен: проверьте состояние объекта перед повторной попыткой.");
        }
        finally
        {
            cb.ForgetChange(id);
        }

        var error = tcs.Task.Result;
        if (error != null)
            throw new InvalidOperationException($"Сервер отклонил изменение: {error}");
        return id;
    }

    /// <summary>
    /// Вызывает серверную команду и ждёт NotifyCommandResult. null — результат не пришёл за timeoutMs.
    /// </summary>
    public async Task<(bool Success, byte[] Data)?> InvokeCommandAsync(
        string commandName, Guid requestId, byte[] data, int timeoutMs = 15000)
    {
        EnsureConnected();
        var cb = _callback ?? throw new InvalidOperationException(NotConnectedMessage);
        var tcs = cb.CommandResult(requestId);
        try
        {
            await ServerApi.InvokeCommandAsync(commandName, requestId, data);
            return await tcs.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
        }
        catch (TimeoutException) when (!tcs.Task.IsCompleted)
        {
            return null;
        }
        finally
        {
            cb.ForgetCommand(requestId);
        }
    }

    public void Disconnect()
    {
        ResetAdminClient();

        try { _client?.Disconnect(); } catch { }
        try { (_client as IDisposable)?.Dispose(); } catch { }

        _client = null;
        _rawServerApi = null;
        _rawSyncServerApi = null;
        _authApi = null;
        _rawFileApi = null;
        _rawMessagesApi = null;
        _proxySyncServerApi = null;
        _proxyServerApi = null;
        _proxyFileApi = null;
        _proxyMessagesApi = null;
        _backend = null;
        _storageProvider = null;
        _metadata = null;
        _dbInfo = null;
        _databaseName = null;
        _callback = null;
        _messageCallback = null;
    }

    public void Dispose() => Disconnect();

    public void EnsureConnected()
    {
        // Под тем же lock, что DoReconnect/Connect: пока другой поток переподключается (клиент временно null),
        // параллельные вызовы ждут, а не запускают конкурирующие логины.
        lock (_reconnectLock)
        {
            if (IsConnected)
            {
                if (_backend == null)
                    RebuildBackend();
                if (_metadataStale)
                    RefreshMetadata();
                return;
            }

            Reconnect();
        }
    }

    /// <summary>
    /// Перечитывает метаданные после NotifyDMetadataChangeset (или записи через админ-API)
    /// и пересоздаёт Backend, который кеширует типы. При сбое остаёмся на старых данных и повторим при следующем вызове.
    /// </summary>
    private void RefreshMetadata()
    {
        _metadataStale = false;
        try
        {
            var api = _rawServerApi!;
            var info = Task.Run(() => api.OpenDatabaseAsync()).GetAwaiter().GetResult();
            var meta = Task.Run(() => api.GetMetadataAsync(info?.MetadataVersion ?? 0)).GetAwaiter().GetResult();
            _dbInfo = info;
            _metadata = meta;
            RebuildBackend();
        }
        catch (Exception ex)
        {
            _metadataStale = true;
            Console.Error.WriteLine($"[PilotMCP] Не удалось обновить метаданные: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void Reconnect()
    {
        if (!string.IsNullOrEmpty(_savedServerUrl) && !string.IsNullOrEmpty(_savedLogin))
        {
            Console.Error.WriteLine("[PilotMCP] Переподключение (сохранённые креды)...");
            Connect(_savedServerUrl!, _savedLogin!, _savedPassword ?? "", _savedDatabase, _savedLicenseType);
            Console.Error.WriteLine("[PilotMCP] Переподключение успешно.");
            return;
        }

        var envUrl = Environment.GetEnvironmentVariable("PILOT_SERVER_URL");
        var envLogin = Environment.GetEnvironmentVariable("PILOT_LOGIN");
        var envPassword = Environment.GetEnvironmentVariable("PILOT_PASSWORD");
        var envDatabase = Environment.GetEnvironmentVariable("PILOT_DATABASE");
        var envLicense = Environment.GetEnvironmentVariable("PILOT_LICENSE_TYPE");
        int licenseType = 100;
        if (!string.IsNullOrEmpty(envLicense))
            int.TryParse(envLicense, out licenseType);

        if (!string.IsNullOrEmpty(envUrl) && !string.IsNullOrEmpty(envLogin) && !string.IsNullOrEmpty(envPassword))
        {
            Console.Error.WriteLine("[PilotMCP] Автоподключение из переменных окружения...");
            Connect(envUrl, envLogin, envPassword, envDatabase, licenseType);
            Console.Error.WriteLine("[PilotMCP] Автоподключение успешно.");
            return;
        }

        throw new InvalidOperationException(
            "Не подключено к серверу Pilot. Задайте переменные окружения PILOT_SERVER_URL, PILOT_LOGIN, PILOT_PASSWORD, PILOT_DATABASE.");
    }

    private class ServerCallback : IServerCallback
    {
        private readonly ConcurrentDictionary<Guid, TaskCompletionSource<DSearchResult>> _searchWaiters = new();
        // null в результате = успех, строка = текст ошибки сервера. GetOrAdd с обеих сторон:
        // ответ может прийти раньше, чем вызывающий начнёт ждать.
        private readonly ConcurrentDictionary<Guid, TaskCompletionSource<string?>> _changeResults = new();
        private readonly ConcurrentDictionary<Guid, TaskCompletionSource<(bool, byte[])?>> _commandResults = new();
        private readonly Action _onMetadataChanged;

        public ServerCallback(Action onMetadataChanged) => _onMetadataChanged = onMetadataChanged;

        private static TaskCompletionSource<T> NewTcs<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void PrepareForSearch(Guid searchId) => _searchWaiters[searchId] = NewTcs<DSearchResult>();

        public void ForgetSearch(Guid searchId) => _searchWaiters.TryRemove(searchId, out _);

        /// <summary>Ждёт результат поиска; по таймауту бросает TimeoutException (а не возвращает «пусто»).</summary>
        public async Task<DSearchResult?> WaitForSearchResultAsync(Guid searchId, int timeoutMs)
        {
            if (!_searchWaiters.TryGetValue(searchId, out var tcs))
                return null;

            try
            {
                return await tcs.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
            }
            catch (TimeoutException)
            {
                throw new TimeoutException($"Сервер не вернул результат поиска за {timeoutMs / 1000} с.");
            }
        }

        public void NotifySearchResult(DSearchResult searchResult)
        {
            if (_searchWaiters.TryGetValue(searchResult.SearchDefinitionId, out var tcs))
            {
                tcs.TrySetResult(searchResult);
            }
        }

        public TaskCompletionSource<string?> ChangeResult(Guid identity) => _changeResults.GetOrAdd(identity, _ => NewTcs<string?>());
        public void ForgetChange(Guid identity) => _changeResults.TryRemove(identity, out _);
        public TaskCompletionSource<(bool, byte[])?> CommandResult(Guid requestId) => _commandResults.GetOrAdd(requestId, _ => NewTcs<(bool, byte[])?>());
        public void ForgetCommand(Guid requestId) => _commandResults.TryRemove(requestId, out _);

        public void NotifyChangeset(DChangeset changeset) { }
        public void NotifyOrganisationUnitChangeset(OrganisationUnitChangeset changeset) { }
        public void NotifyPersonChangeset(PersonChangeset changeset) { }
        public void NotifyDMetadataChangeset(DMetadataChangeset changeset) => _onMetadataChanged();
        public void NotifyGeometrySearchResult(DGeometrySearchResult searchResult) { }
        public void NotifyDNotificationChangeset(DNotificationChangeset changeset) { }
        public void NotifyCommandResult(Guid requestId, byte[] data, ServerCommandResult result)
            => CommandResult(requestId).TrySetResult((result == ServerCommandResult.Success, data));
        public void NotifyChangeAsyncCompleted(DChangeset changeset) => ChangeResult(changeset.Identity).TrySetResult(null);
        public void NotifyChangeAsyncError(Guid identity, ProtoExceptionInfo exception)
            => ChangeResult(identity).TrySetResult(ProtoExceptionInfo.Deserialize(exception).Message);
        public void NotifyCustomNotification(string name, byte[] data) { }
        public void NotifyAccessChangeset(Guid objectId) { }
        public void NotifySettingsChangeset(string settingKey) { }
    }

    private class ConnectionLostHandler : IConnectionLostListener
    {
        private readonly PilotConnection _owner;
        public ConnectionLostHandler(PilotConnection owner) => _owner = owner;

        public void ConnectionLost(Exception ex)
        {
            Console.Error.WriteLine($"[PilotMCP] CONNECTION LOST: {ex.GetType().Name}: {ex.Message}");
            _owner._connectionLost = true;
        }
    }

    private class AdminConnectionLostHandler : IConnectionLostListener
    {
        private readonly PilotConnection _owner;
        public AdminConnectionLostHandler(PilotConnection owner) => _owner = owner;

        public void ConnectionLost(Exception ex)
        {
            Console.Error.WriteLine($"[PilotMCP] ADMIN CONNECTION LOST: {ex.GetType().Name}: {ex.Message}");
            _owner._adminConnectionLost = true;
        }
    }

    private class AdminCallback : IServerAdminCallback
    {
        public void NotifyDatabaseChanged(DatabaseChangeset changeset) { }
        public void NotifyPersonChanged(string database, PersonChangeset changeset) { }
        public void NotifyOrganisationUnitChanged(string database, OrganisationUnitChangeset changeset) { }
        public void NotifyDMetadataChanged(string database, DMetadataChangeset changeset) { }
        public void NotifyConnectionCountFromServer(int current, int max) { }
        public void NotifyReservationsChanged() { }
        public void NotifyIndexingStateChanged(string database, ushort state) { }
        public void NotifySessionsFromServer(int current, int max, int reserved, Guid licenseId) { }
        public void NotifyLicenseChanged() { }
    }

    private class MessageCallback : IMessageCallback
    {
        public void NotifyMessageCreated(NotifiableDMessage message) { }
        public void NotifyMessagesCreated(List<NotifiableDMessage> messages) { }
        public void NotifyTypingMessage(Guid chatId, int personId) { }
        public void NotifyOnline(int personId) { }
        public void NotifyOffline(int personId) { }
        public void CreateNotification(DNotification notification) { }
        public void UpdateLastMessageDate(DateTime maxDate) { }
    }
}
