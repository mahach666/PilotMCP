using Ascon.Pilot.DataClasses;
using static Ascon.Pilot.Common.DMessageExtensions;

namespace PilotMCP.Tests;

/// <summary>Чистые unit-тесты MessageHelpers (без сервера).</summary>
public class MessageHelpersTests
{
    private static DMessage Text(string text, bool markdown, MessageType type = MessageType.TextMessage, DateTime? server = null)
    {
        var m = new DMessage { Id = Guid.NewGuid(), Type = type, ServerDate = server, LocalDate = server ?? DateTime.UtcNow };
        m.SetTextData(new DTextMessageData { Text = text, IsMarkdown = markdown });
        return m;
    }

    [Fact]
    public void FormatText_PlainText_ReturnedAsIs()
        => Assert.Equal("hello", MessageHelpers.FormatText(Text("hello", false)));

    [Fact]
    public void FormatText_Markdown_IsFlagged()
        => Assert.Equal("**b** [markdown]", MessageHelpers.FormatText(Text("**b**", true)));

    [Fact]
    public void FormatText_UsesLatestEditAndMarksIt()
    {
        var m = Text("old", false);
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        m.RelatedMessages.Add(Text("second", false, MessageType.EditTextMessage, t0.AddMinutes(2)));
        m.RelatedMessages.Add(Text("first", false, MessageType.EditTextMessage, t0.AddMinutes(1)));

        Assert.Equal("second (ред.)", MessageHelpers.FormatText(m));
    }

    [Fact]
    public void FormatText_NonTextMessage_ShowsType()
        => Assert.Equal("[ChatCreation]", MessageHelpers.FormatText(new DMessage { Type = MessageType.ChatCreation, Data = new byte[] { 1 } }));

    [Fact]
    public void TryParseGuids_RejectsAnyBadItem()
    {
        var g = Guid.NewGuid();
        Assert.True(MessageHelpers.TryParseGuids($"{g}, ", out var ok, out _));
        Assert.Equal(new[] { g }, ok);
        Assert.False(MessageHelpers.TryParseGuids($"{g},zzz", out _, out var bad));
        Assert.Equal("zzz", bad);
    }

    [Fact]
    public void TryParseDeadline_IsEndOfLocalDay_AndStrictFormat()
    {
        Assert.True(MessageHelpers.TryParseDeadline("2026-12-01", out var utc));
        var local = utc.ToLocalTime();
        Assert.Equal(new DateTime(2026, 12, 1, 23, 59, 59), local);
        Assert.False(MessageHelpers.TryParseDeadline("01.12.2026", out _));
        Assert.False(MessageHelpers.TryParseDeadline("12/01/2026", out _));
    }
}
