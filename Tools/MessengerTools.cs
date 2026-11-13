using Ascon.Pilot.DataClasses;
using static Ascon.Pilot.Common.DMessageExtensions;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text;

namespace PilotMCP;

[McpServerToolType]
public class MessengerTools
{
    private readonly PilotConnection _connection;
    public MessengerTools(PilotConnection connection) => _connection = connection;

    private void EnsureMessagesApi()
    {
        _connection.EnsureConnected();
        if (_connection.MessagesApi == null)
            throw new InvalidOperationException("API сообщений недоступно.");
    }

    [McpServerTool, Description(
        "Получить список чатов. Возвращает последние чаты пользователя.")]
    public Task<string> GetChats(
        [Description("Количество чатов (по умолчанию 20)")] int count = 20)
    {
        return ToolRunner.RunAsync(_connection, "GetChats", $"count={count}", async () =>
        {
            EnsureMessagesApi();
            var api = _connection.MessagesApi!;
            api.Open(100, new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));

            var personId = _connection.CurrentPersonId;
            var fromDate = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var chats = api.GetChats(personId, fromDate, DateTime.UtcNow, count, false);

            if (chats.Count == 0)
                return ToolResult.Ok("Чатов не найдено.");

            var people = await _connection.ServerApi.LoadPeopleAsync();
            var personMap = people.ToDictionary(p => p.Id, p => p.DisplayName ?? p.Login ?? $"#{p.Id}");

            var sb = new StringBuilder();
            sb.AppendLine($"Чаты ({chats.Count}):");
            foreach (var chat in chats)
            {
                var lastMsg = chat.LastMessage;
                var author = lastMsg != null ? personMap.GetValueOrDefault(lastMsg.CreatorId, $"#{lastMsg.CreatorId}") : "";
                var msgText = lastMsg != null ? SafeGetText(lastMsg) : "";
                var preview = msgText.Length > 80 ? msgText[..80] + "..." : msgText;
                sb.AppendLine($"  [{chat.Chat.Id}] {chat.Chat.Name ?? "Без названия"}");
                if (lastMsg != null)
                    sb.AppendLine($"    Последнее: [{lastMsg.GetActualDateTime():MM-dd HH:mm}] {author}: {preview}");
            }

            return ToolResult.Ok(sb.ToString());
        });
    }

    [McpServerTool, Description(
        "Прочитать сообщения из чата (от старых к новым внутри страницы). Пагинация: skip=0 — самые новые count сообщений, " +
        "skip=count — предыдущая страница и т.д. Правки сообщений подставляются в текст с пометкой (ред.), реакции и markdown помечаются.")]
    public Task<string> GetMessages(
        [Description("GUID чата")] string chatId,
        [Description("Количество сообщений (по умолчанию 30)")] int count = 30,
        [Description("Пропустить N самых новых сообщений (для пагинации назад по истории)")] int skip = 0,
        [Description("Макс. длина текста одного сообщения (0 = без лимита)")] int maxMessageLength = 500)
    {
        return ToolRunner.RunAsync(_connection, "GetMessages", $"chat={chatId}, count={count}, skip={skip}", async () =>
        {
            EnsureMessagesApi();
            if (!Guid.TryParse(chatId, out var guid))
                return ToolResult.Error($"Некорректный GUID: {chatId}");

            if (count <= 0 || skip < 0)
                return ToolResult.Error("count должен быть > 0, skip >= 0.");

            var api = _connection.MessagesApi!;

            List<DMessage> page;
            bool hasMore;
            try
            {
                var fromDate = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                // total сервер всегда возвращает 0: берём на одно сообщение больше, чтобы знать, есть ли продолжение
                var (messages, _) = api.GetMessages(guid, fromDate, DateTime.UtcNow, skip + count + 1);
                hasMore = messages.Count > skip + count;
                page = messages
                    .OrderByDescending(m => m.GetActualDateTime())
                    .Skip(skip)
                    .Take(count)
                    .Reverse()
                    .ToList();
            }
            catch (Exception getEx) when (getEx is PilotSecurityAccessDeniedException or PilotServerNotFoundException)
            {
                ToolLogger.LogError("GetMessages.api", $"chat={chatId}", getEx);
                return ToolResult.Error($"Чат недоступен: {getEx.Message}");
            }
            catch (Exception getEx)
            {
                ToolLogger.LogError("GetMessages.api", $"chat={chatId}", getEx);
                return await GetMessagesFallback(guid, count, skip, maxMessageLength, getEx);
            }

            if (page.Count == 0)
                return ToolResult.Ok("Сообщений не найдено.");

            var people = await _connection.ServerApi.LoadPeopleAsync();
            var personMap = people.ToDictionary(p => p.Id, p => p.DisplayName ?? p.Login ?? $"#{p.Id}");

            var sb = new StringBuilder();
            sb.AppendLine($"Сообщения чата ({page.Count}, skip={skip}):");
            foreach (var msg in page)
            {
                var who = personMap.GetValueOrDefault(msg.CreatorId, $"#{msg.CreatorId}");
                var msgText = SafeGetText(msg);
                if (maxMessageLength > 0)
                    msgText = Helpers.Truncate(msgText, maxMessageLength);
                sb.AppendLine($"  [{msg.GetActualDateTime():MM-dd HH:mm}] (id:{msg.Id}) {who}: {msgText}");
            }
            if (hasMore)
                sb.AppendLine($"  ... есть более ранние сообщения (используйте skip={skip + page.Count})");

            return ToolResult.Ok(sb.ToString());
        });
    }

    /// <summary>
    /// Fallback: когда GetMessages падает на сервере, получаем сообщения через GetMessage по одному.
    /// </summary>
    private async Task<string> GetMessagesFallback(Guid chatId, int count, int skip, int maxMessageLength, Exception getEx)
    {
        var api = _connection.MessagesApi!;

        var chatInfo = api.GetChat(chatId);
        var messageIds = chatInfo.Relations
            .Where(r => r.MessageId.HasValue)
            .Select(r => r.MessageId!.Value)
            .ToList();

        try
        {
            var searchDef = new DMessageSearchDefinition
            {
                Id = Guid.NewGuid(),
                Request = new DMessageSearchRequest { SearchString = "", MaxResults = skip + count }
            };
            searchDef.Request.ChatIds.Add(chatId);
            var searchResult = api.SearchMessages(searchDef);
            if (searchResult.Found != null)
                foreach (var id in searchResult.Found)
                    if (!messageIds.Contains(id)) messageIds.Add(id);
        }
        catch (Exception searchEx) { ToolLogger.LogError("GetMessages.fallback.search", $"chat={chatId}", searchEx); }

        if (messageIds.Count == 0)
            return ToolResult.Error(
                $"GetMessages завершился ошибкой ({getEx.Message}), fallback не нашёл ID сообщений.");

        var people = await _connection.ServerApi.LoadPeopleAsync();
        var personMap = people.ToDictionary(p => p.Id, p => p.DisplayName ?? p.Login ?? $"#{p.Id}");

        var messages = new List<(Guid id, DateTime date, string who, string text, string type)>();
        foreach (var msgId in messageIds)
        {
            try
            {
                var msg = api.GetMessage(msgId);
                if (msg == null) continue;
                var who = personMap.GetValueOrDefault(msg.CreatorId, $"#{msg.CreatorId}");
                var text = SafeGetText(msg);
                if (maxMessageLength > 0) text = Helpers.Truncate(text, maxMessageLength);
                messages.Add((msg.Id, msg.GetActualDateTime(), who, text, msg.Type.ToString()));
            }
            catch (Exception msgEx) { ToolLogger.LogError("GetMessages.fallback.message", $"msg={msgId}", msgEx); }
        }

        var page = messages.OrderByDescending(m => m.date).Skip(skip).Take(count).Reverse().ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"Сообщения чата (fallback, {page.Count} найдено, GetMessages недоступен):");
        foreach (var (id, date, who, text, type) in page)
            sb.AppendLine($"  [{date:MM-dd HH:mm}] (id:{id}) {who}: {text}" + (type != "TextMessage" ? $" [{type}]" : ""));

        return ToolResult.Ok(sb.ToString());
    }

    private static string SafeGetText(INMessage msg) => MessageHelpers.FormatText(msg);

    [McpServerTool, Description(
        "Отправить сообщение в чат.")]
    public string SendMessage(
        [Description("GUID чата")] string chatId,
        [Description("Текст сообщения")] string text)
    {
        return ToolRunner.RunWrite(_connection, "SendMessage", $"chat={chatId}", () =>
        {
            EnsureMessagesApi();
            if (!Guid.TryParse(chatId, out var guid))
                return ToolResult.Error($"Некорректный GUID: {chatId}");

            var api = _connection.MessagesApi!;
            var msg = new DMessage
            {
                Id = Guid.NewGuid(),
                ChatId = guid,
                CreatorId = _connection.CurrentPersonId,
                LocalDate = DateTime.UtcNow,
                Type = MessageType.TextMessage
            };
            msg.SetTextData(text, false);
            api.SendMessage(msg);

            return ToolResult.Ok($"Сообщение отправлено. ID: {msg.Id}");
        });
    }

    [McpServerTool, Description("Создать групповой чат.")]
    public string CreateChat(
        [Description("Название чата")] string name,
        [Description("Описание чата (необязательно)")] string description = "")
    {
        return ToolRunner.RunWrite(_connection, "CreateChat", $"name={name}", () =>
        {
            EnsureMessagesApi();
            var api = _connection.MessagesApi!;
            var chatId = Guid.NewGuid();

            var chatMsg = new DMessage
            {
                Id = Guid.NewGuid(),
                ChatId = chatId,
                CreatorId = _connection.CurrentPersonId,
                LocalDate = DateTime.UtcNow,
                Type = MessageType.ChatCreation
            };
            var chat = new DChat
            {
                Id = chatId,
                Name = name,
                Description = description ?? "",
                CreatorId = _connection.CurrentPersonId,
                Type = ChatKind.Group,
                CreationDateUtc = DateTime.UtcNow
            };
            chatMsg.SetChatData(chat);
            api.SendMessage(chatMsg);

            return ToolResult.Ok($"Чат создан: {name}\nID: {chatId}");
        });
    }

    [McpServerTool, Description("Редактировать ранее отправленное сообщение.")]
    public string EditMessage(
        [Description("GUID чата")] string chatId,
        [Description("GUID сообщения для редактирования")] string messageId,
        [Description("Новый текст сообщения")] string newText)
    {
        return ToolRunner.RunWrite(_connection, "EditMessage", $"chat={chatId}, msg={messageId}", () =>
        {
            EnsureMessagesApi();
            if (!Guid.TryParse(chatId, out var chatGuid))
                return ToolResult.Error($"Некорректный GUID чата: {chatId}");
            if (!Guid.TryParse(messageId, out var msgGuid))
                return ToolResult.Error($"Некорректный GUID сообщения: {messageId}");

            var api = _connection.MessagesApi!;
            var msg = new DMessage
            {
                Id = Guid.NewGuid(),
                ChatId = chatGuid,
                CreatorId = _connection.CurrentPersonId,
                LocalDate = DateTime.UtcNow,
                Type = MessageType.EditTextMessage,
                RelatedMessageId = msgGuid
            };
            msg.SetTextData(newText, false);
            api.SendMessage(msg);

            return ToolResult.Ok($"Сообщение {messageId} отредактировано.");
        });
    }

    [McpServerTool, Description("Ответить на конкретное сообщение в чате.")]
    public string ReplyMessage(
        [Description("GUID чата")] string chatId,
        [Description("GUID сообщения, на которое отвечаем")] string replyToMessageId,
        [Description("Текст ответа")] string text)
    {
        return ToolRunner.RunWrite(_connection, "ReplyMessage", $"chat={chatId}, reply={replyToMessageId}", () =>
        {
            EnsureMessagesApi();
            if (!Guid.TryParse(chatId, out var chatGuid))
                return ToolResult.Error($"Некорректный GUID чата: {chatId}");
            if (!Guid.TryParse(replyToMessageId, out var replyGuid))
                return ToolResult.Error($"Некорректный GUID сообщения: {replyToMessageId}");

            var api = _connection.MessagesApi!;
            var msg = new DMessage
            {
                Id = Guid.NewGuid(),
                ChatId = chatGuid,
                CreatorId = _connection.CurrentPersonId,
                LocalDate = DateTime.UtcNow,
                Type = MessageType.MessageAnswer,
                RelatedMessageId = replyGuid
            };
            msg.SetTextData(text, false);
            api.SendMessage(msg);

            return ToolResult.Ok($"Ответ на {replyToMessageId} отправлен.");
        });
    }

    [McpServerTool, Description(
        "Проверить, онлайн ли пользователь.")]
    public string CheckOnline(
        [Description("ID пользователя (personId)")] int personId)
    {
        return ToolRunner.Run(_connection, "CheckOnline", $"person={personId}", () =>
        {
            EnsureMessagesApi();
            var online = _connection.MessagesApi!.CheckIsOnline(personId);
            return ToolResult.Ok(online ? $"Пользователь #{personId} онлайн" : $"Пользователь #{personId} оффлайн");
        });
    }

    [McpServerTool, Description(
        "Получить информацию о конкретном чате по его GUID.")]
    public string GetChat(
        [Description("GUID чата")] string chatId)
    {
        return ToolRunner.Run(_connection, "GetChat", $"chat={chatId}", () =>
        {
            EnsureMessagesApi();
            if (!Guid.TryParse(chatId, out var guid))
                return ToolResult.Error($"Некорректный GUID: {chatId}");

            var info = _connection.MessagesApi!.GetChat(guid);
            var chat = info.Chat;
            if (chat == null || chat.Id == Guid.Empty)
                return ToolResult.Error($"Чат {chatId} не найден.");
            var sb = new StringBuilder();
            sb.AppendLine($"Чат: {chat.Name ?? "Без названия"}");
            sb.AppendLine($"  ID: {chat.Id}");
            sb.AppendLine($"  Тип: {chat.Type}");
            sb.AppendLine($"  Создатель (personId): {chat.CreatorId}");
            sb.AppendLine($"  Создан: {chat.CreationDateUtc:yyyy-MM-dd HH:mm:ss}");
            if (!string.IsNullOrEmpty(chat.Description)) sb.AppendLine($"  Описание: {chat.Description}");
            sb.AppendLine($"  Непрочитанных: {info.UnreadMessagesNumber}");
            sb.AppendLine($"  IsViewerOnly: {info.IsViewerOnly}");
            if (info.Relations.Any())
            {
                sb.AppendLine($"  Связи ({info.Relations.Count}):");
                foreach (var rel in info.Relations)
                    sb.AppendLine($"    - Тип: {rel.Type}, ObjectId: {rel.ObjectId}" +
                                  (rel.MessageId.HasValue ? $", MessageId: {rel.MessageId}" : ""));
            }

            return ToolResult.Ok(sb.ToString());
        });
    }

    [McpServerTool, Description(
        "Получить личный чат с конкретным пользователем (если он уже существует; сам инструмент чат не создаёт).")]
    public string GetPersonalChat(
        [Description("ID пользователя (personId)")] int personId)
    {
        return ToolRunner.Run(_connection, "GetPersonalChat", $"person={personId}", () =>
        {
            EnsureMessagesApi();
            var info = _connection.MessagesApi!.GetPersonalChat(personId);
            var chat = info.Chat;
            if (chat == null || chat.Id == Guid.Empty)
                return ToolResult.Error($"Личного чата с #{personId} ещё нет.");
            var sb = new StringBuilder();
            sb.AppendLine($"Личный чат с #{personId}:");
            sb.AppendLine($"  ChatId: {chat.Id}");
            sb.AppendLine($"  Непрочитанных: {info.UnreadMessagesNumber}");

            return ToolResult.Ok(sb.ToString());
        });
    }

    [McpServerTool, Description(
        "Получить участников чата.")]
    public Task<string> GetChatMembers(
        [Description("GUID чата")] string chatId)
    {
        return ToolRunner.RunAsync(_connection, "GetChatMembers", $"chat={chatId}", async () =>
        {
            EnsureMessagesApi();
            if (!Guid.TryParse(chatId, out var guid))
                return ToolResult.Error($"Некорректный GUID: {chatId}");

            var members = _connection.MessagesApi!.GetChatMembers(guid, new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));

            if (members.Count == 0)
                return ToolResult.Ok("Участников не найдено.");

            var people = await _connection.ServerApi.LoadPeopleAsync();
            var personMap = people.ToDictionary(p => p.Id, p => p.DisplayName ?? p.Login ?? $"#{p.Id}");

            var sb = new StringBuilder();
            sb.AppendLine($"Участники чата ({members.Count}):");
            foreach (var m in members)
            {
                var name = personMap.GetValueOrDefault(m.PersonId, $"#{m.PersonId}");
                var flags = new List<string>();
                if (m.IsAdmin) flags.Add("admin");
                if (m.IsDeleted) flags.Add("удалён");
                if (m.IsViewerOnly) flags.Add("только просмотр");
                if (!m.IsNotifiable) flags.Add("без уведомлений");
                var flagsStr = flags.Any() ? $" ({string.Join(", ", flags)})" : "";
                sb.AppendLine($"  #{m.PersonId} {name}{flagsStr}");
            }

            return ToolResult.Ok(sb.ToString());
        });
    }

    [McpServerTool, Description(
        "Получить одно сообщение по его GUID.")]
    public Task<string> GetMessage(
        [Description("GUID сообщения")] string messageId)
    {
        return ToolRunner.RunAsync(_connection, "GetMessage", $"msg={messageId}", async () =>
        {
            EnsureMessagesApi();
            if (!Guid.TryParse(messageId, out var guid))
                return ToolResult.Error($"Некорректный GUID: {messageId}");

            var msg = _connection.MessagesApi!.GetMessage(guid);
            if (msg == null)
                return ToolResult.Error("Сообщение не найдено.");

            var people = await _connection.ServerApi.LoadPeopleByIdsAsync(new[] { msg.CreatorId });
            var who = people.Count > 0 ? people[0].DisplayName ?? people[0].Login : $"#{msg.CreatorId}";
            var text = SafeGetText(msg);

            var sb = new StringBuilder();
            sb.AppendLine($"Сообщение {msg.Id}:");
            sb.AppendLine($"  Автор: {who} (#{msg.CreatorId})");
            sb.AppendLine($"  Дата: {msg.GetActualDateTime():yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"  Тип: {msg.Type}");
            sb.AppendLine($"  Чат: {msg.ChatId}");
            if (msg.RelatedMessageId.HasValue) sb.AppendLine($"  Ответ на: {msg.RelatedMessageId}");
            sb.AppendLine($"  Текст: {text}");

            return ToolResult.Ok(sb.ToString());
        });
    }

    [McpServerTool, Description(
        "Поиск сообщений по тексту в указанных чатах (если chatIds пуст — во всех чатах текущего пользователя).")]
    public string SearchMessages(
        [Description("Строка поиска")] string query,
        [Description("GUID чатов через запятую (если пусто — поиск по всем)")] string chatIds = "",
        [Description("Макс. результатов (по умолчанию 50)")] int maxResults = 50)
    {
        return ToolRunner.Run(_connection, "SearchMessages", $"query={query}, chats={chatIds}", () =>
        {
            EnsureMessagesApi();
            if (string.IsNullOrWhiteSpace(query))
                return ToolResult.Error("Строка поиска не может быть пустой.");
            if (maxResults <= 0)
                return ToolResult.Error("maxResults должен быть > 0.");
            if (!MessageHelpers.TryParseGuids(chatIds, out var chatGuids, out var badGuid))
                return ToolResult.Error($"Некорректный GUID чата: {badGuid}");

            var api = _connection.MessagesApi!;
            if (chatGuids.Count == 0)
            {
                // сервер падает на пустом ChatIds — перечисляем чаты пользователя сами
                var from = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                api.Open(100, from);
                chatGuids = api.GetChats(_connection.CurrentPersonId, from, DateTime.UtcNow, 2000, false)
                    .Select(c => c.Chat.Id).ToList();
                if (chatGuids.Count == 0)
                    return ToolResult.Ok("У пользователя нет чатов, искать негде.");
            }

            var request = new DMessageSearchRequest
            {
                SearchString = query,
                MaxResults = maxResults
            };
            request.ChatIds.AddRange(chatGuids);
            var searchDef = new DMessageSearchDefinition
            {
                Id = Guid.NewGuid(),
                Request = request
            };

            var searchResult = _connection.MessagesApi!.SearchMessages(searchDef);

            if (searchResult.Found == null || searchResult.Found.Count == 0)
                return ToolResult.Ok($"По запросу \"{query}\" ничего не найдено.");

            var sb = new StringBuilder();
            sb.AppendLine($"Результаты поиска \"{query}\" (показано {searchResult.Found.Count}, всего найдено {searchResult.TotalResults}):");
            foreach (var msgId in searchResult.Found)
            {
                try
                {
                    var msg = _connection.MessagesApi!.GetMessage(msgId);
                    if (msg == null) continue;
                    var text = SafeGetText(msg);
                    var preview = text.Length > 100 ? text[..100] + "..." : text;
                    sb.AppendLine($"  [{msg.GetActualDateTime():MM-dd HH:mm}] #{msg.CreatorId} в {msg.ChatId}: {preview}");
                }
                catch (Exception msgEx)
                {
                    ToolLogger.LogError("SearchMessages.message", $"msg={msgId}", msgEx);
                    sb.AppendLine($"  [ошибка чтения сообщения {msgId}: {msgEx.Message}]");
                }
            }

            return ToolResult.Ok(sb.ToString());
        });
    }

    [McpServerTool, Description(
        "Получить чаты, связанные с объектом (например, обсуждение задачи или документа).")]
    public string GetRelatedChats(
        [Description("GUID объекта")] string objectId,
        [Description("Тип связи: Relation или Attach (по умолчанию Relation)")] string relationType = "Relation")
    {
        return ToolRunner.Run(_connection, "GetRelatedChats", $"obj={objectId}, type={relationType}", () =>
        {
            EnsureMessagesApi();
            if (!Guid.TryParse(objectId, out var guid))
                return ToolResult.Error($"Некорректный GUID: {objectId}");

            if (!Enum.TryParse<ChatRelationType>(relationType, ignoreCase: true, out var chatRelType))
                return ToolResult.Error($"Неизвестный тип: {relationType}. Допустимые: Relation, Attach.");

            var chats = _connection.MessagesApi!.GetRelatedChats(_connection.CurrentPersonId, guid, chatRelType);

            if (chats.Count == 0)
                return ToolResult.Ok($"Чатов, связанных с объектом {objectId}, не найдено.");

            var sb = new StringBuilder();
            sb.AppendLine($"Чаты, связанные с {objectId} ({chats.Count}):");
            foreach (var item in chats)
            {
                var chatInfo = item.DChatInfo;
                var chat = chatInfo.Chat;
                sb.AppendLine($"  [{chat.Id}] {chat.Name ?? "Без названия"} ({chat.Type})");
                sb.AppendLine($"    Непрочитанных: {chatInfo.UnreadMessagesNumber}");
            }

            return ToolResult.Ok(sb.ToString());
        });
    }
}
