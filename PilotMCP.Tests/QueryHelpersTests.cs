namespace PilotMCP.Tests;

/// <summary>Чистые unit-тесты хелперов поиска и файловых инструментов (без сервера).</summary>
public class QueryHelpersTests
{
    [Fact]
    public void EscapeLucene_EscapesEverySpecialChar()
    {
        Assert.Equal(@"a\:b", QueryHelpers.EscapeLucene("a:b"));
        Assert.Equal(@"\(x\)", QueryHelpers.EscapeLucene("(x)"));
        Assert.Equal(@"\-\+\*\?\\\/\""", QueryHelpers.EscapeLucene("-+*?\\/\""));
        Assert.Equal("Договор", QueryHelpers.EscapeLucene("Договор"));
        // операторы Lucene в верхнем регистре не должны уходить в запрос как операторы
        Assert.Equal("and", QueryHelpers.EscapeLucene("AND"));
        Assert.Equal("or", QueryHelpers.EscapeLucene("OR"));
        Assert.Equal("not", QueryHelpers.EscapeLucene("NOT"));
        Assert.Equal("Or", QueryHelpers.EscapeLucene("Or"));
    }

    [Fact]
    public void QuoteLucene_KeepsMultiWordValueAsOnePhrase()
    {
        Assert.Equal("\"Рабочая документация\"", QueryHelpers.QuoteLucene("Рабочая документация"));
        Assert.Equal("\"a \\\"b\\\" \\\\c\"", QueryHelpers.QuoteLucene("a \"b\" \\c"));
    }

    [Fact]
    public void EscapedWords_SplitsAndEscapes_EmptyForBlank()
    {
        Assert.Equal(new[] { "a", @"b\:c" }, QueryHelpers.EscapedWords("  a   b:c "));
        Assert.Empty(QueryHelpers.EscapedWords("   "));
    }

    [Fact]
    public void ParseRange_DateOnlyUpperBoundIncludesWholeDay_AndIsCultureIndependent()
    {
        var old = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("ru-RU");
        try
        {
            Assert.Null(QueryHelpers.ParseRange("2025-01-01", "2025-01-31", "from", "to", out var f, out var t));
            Assert.Equal(new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc), f);
            Assert.Equal(new DateTime(2025, 1, 31, 23, 59, 59, DateTimeKind.Utc), t);
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = old; }
    }

    [Fact]
    public void ParseRange_ExplicitTimeUpperBoundIsKept_EmptyMeansOpen()
    {
        Assert.Null(QueryHelpers.ParseRange("", "2025-01-31T10:00:00Z", "from", "to", out var f, out var t));
        Assert.Equal(DateTime.MinValue, f);
        Assert.Equal(new DateTime(2025, 1, 31, 10, 0, 0, DateTimeKind.Utc), t);
        Assert.Null(QueryHelpers.ParseRange("", "", "from", "to", out _, out var t2));
        Assert.Equal(DateTime.MaxValue, t2);
    }

    [Theory]
    [InlineData("garbage", "")]
    [InlineData("", "32.13.2025")]
    [InlineData("2025-02-01", "2025-01-01")]
    public void ParseRange_InvalidOrInverted_ReturnsError(string from, string to)
        => Assert.NotNull(QueryHelpers.ParseRange(from, to, "from", "to", out _, out _));

    [Fact]
    public void CsvEscape_QuotesSeparatorQuoteAndLineBreaks()
    {
        Assert.Equal("plain", QueryHelpers.CsvEscape("plain", ";"));
        Assert.Equal("\"a;b\"", QueryHelpers.CsvEscape("a;b", ";"));
        Assert.Equal("\"a\"\"b\"", QueryHelpers.CsvEscape("a\"b", ";"));
        Assert.Equal("\"a\rb\"", QueryHelpers.CsvEscape("a\rb", ";"));
        Assert.Equal("\"a\nb\"", QueryHelpers.CsvEscape("a\nb", ";"));
    }

    [Fact]
    public void PrepareOutput_SamePath_IsErrorAndCreatesDirForOthers()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"qh_{Guid.NewGuid():N}");
        var input = Path.Combine(dir, "in.jsonl");
        Directory.CreateDirectory(dir);
        try
        {
            Assert.NotNull(QueryHelpers.PrepareOutput(input, input));
            if (OperatingSystem.IsWindows())
                Assert.NotNull(QueryHelpers.PrepareOutput(input, Path.Combine(dir, ".", "IN.JSONL")));
            var nested = Path.Combine(dir, "sub", "out.jsonl");
            Assert.Null(QueryHelpers.PrepareOutput(input, nested));
            Assert.True(Directory.Exists(Path.Combine(dir, "sub")));
        }
        finally { Directory.Delete(dir, true); }
    }
}
