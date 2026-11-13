using System.Globalization;
using Ascon.Pilot.DataClasses;
using static Ascon.Pilot.Common.DMessageExtensions;

namespace PilotMCP;

/// <summary>Чистые хелперы мессенджера и задач (без обращения к серверу).</summary>
public static class MessageHelpers
{
    /// <summary>
    /// Текст сообщения для показа: последняя правка (EditTextMessage из RelatedMessages) заменяет оригинал и
    /// помечается «(ред.)», markdown помечается, реакции (MessageReaction) сводятся в «[реакции: ...]».
    /// </summary>
    public static string FormatText(INMessage msg)
    {
        if (msg.Data == null || msg.Data.Length == 0)
            return $"[{msg.Type}]";

        if (msg.Type is not (MessageType.TextMessage or MessageType.EditTextMessage or MessageType.MessageAnswer))
            return $"[{msg.Type}]";

        var related = msg.RelatedMessages?.ToList() ?? new List<INMessage>();
        var source = msg;
        var edited = false;
        var edit = related.Where(r => r.Type == MessageType.EditTextMessage && r.Data is { Length: > 0 })
            .OrderBy(r => r.ServerDate ?? r.LocalDate).LastOrDefault();
        if (edit != null) { source = edit; edited = true; }

        string text;
        bool markdown;
        try
        {
            var data = source.GetMessageData<DTextMessageData>();
            text = data?.Text ?? "";
            markdown = data?.IsMarkdown == true;
        }
        catch (Exception ex)
        {
            return $"[ошибка чтения текста, {source.Data.Length} bytes: {ex.Message}]";
        }

        if (edited) text += " (ред.)";
        if (markdown) text += " [markdown]";

        var reactions = new List<string>();
        foreach (var r in related.Where(r => r.Type == MessageType.MessageReaction && r.Data is { Length: > 0 }))
        {
            try { reactions.AddRange(r.GetMessageData<DMessageReactionData>()?.Emojis ?? new List<string>()); }
            catch (Exception ex) { reactions.Add($"[ошибка реакции: {ex.Message}]"); }
        }
        if (reactions.Count > 0)
            text += " [реакции: " + string.Join(" ", reactions.GroupBy(e => e).Select(g => g.Count() > 1 ? $"{g.Key}x{g.Count()}" : g.Key)) + "]";

        return text;
    }

    /// <summary>Разбирает GUID через запятую. Любой некорректный элемент — ошибка (возвращается его текст).</summary>
    public static bool TryParseGuids(string? csv, out List<Guid> guids, out string? bad)
    {
        guids = new List<Guid>();
        bad = null;
        foreach (var s in (csv ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!Guid.TryParse(s, out var g)) { bad = s; return false; }
            guids.Add(g);
        }
        return true;
    }

    /// <summary>Дедлайн yyyy-MM-dd → конец этого дня (23:59:59 по местному времени, как в клиенте Pilot) в UTC.</summary>
    public static bool TryParseDeadline(string text, out DateTime utc)
    {
        utc = default;
        if (!DateTime.TryParseExact(text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            return false;
        utc = DateTime.SpecifyKind(d.Date.AddHours(23).AddMinutes(59).AddSeconds(59), DateTimeKind.Local).ToUniversalTime();
        return true;
    }
}
