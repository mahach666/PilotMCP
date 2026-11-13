using Ascon.Pilot.DataClasses;
using Ascon.Pilot.Server.Api.Contracts;

namespace PilotMCP.Tests;

/// <summary>Чистые unit-тесты BuildTypedValue и MeasureArchiveFile (без сервера).</summary>
public class ObjectHelpersTests
{
    private static MAttribute Attr(MAttrType t) => new() { Name = "a", Type = t };

    [Fact]
    public void BuildTypedValue_Integer_UsesLongValue()
    {
        var v = Helpers.BuildTypedValue(Attr(MAttrType.Integer), " 42 ");
        Assert.Equal(42L, v.IntValue);
        Assert.Null(v.StrValue);
    }

    [Fact]
    public void BuildTypedValue_DoubleAndDecimal_UseInvariantCulture()
    {
        Assert.Equal(12.5, Helpers.BuildTypedValue(Attr(MAttrType.Double), "12.5").DoubleValue);
        Assert.Equal(3.25m, Helpers.BuildTypedValue(Attr(MAttrType.Decimal), "3.25").DecimalValue);
    }

    [Fact]
    public void BuildTypedValue_DateTime_IsUtc()
    {
        var d = Helpers.BuildTypedValue(Attr(MAttrType.DateTime), "2025-01-31T10:00:00Z").DateValue!.Value;
        Assert.Equal(new DateTime(2025, 1, 31, 10, 0, 0, DateTimeKind.Utc), d);
        Assert.Equal(DateTimeKind.Utc, d.Kind);
    }

    [Fact]
    public void BuildTypedValue_Boolean_AcceptsWordsAndDigits()
    {
        Assert.Equal(1L, Helpers.BuildTypedValue(Attr(MAttrType.Boolean), "true").IntValue);
        Assert.Equal(0L, Helpers.BuildTypedValue(Attr(MAttrType.Boolean), "0").IntValue);
        Assert.Throws<ArgumentException>(() => Helpers.BuildTypedValue(Attr(MAttrType.Boolean), "maybe"));
    }

    [Fact]
    public void BuildTypedValue_UserStateOrgUnitArray()
    {
        var id = Guid.NewGuid();
        Assert.Equal(id, Helpers.BuildTypedValue(Attr(MAttrType.UserState), id.ToString()).GuidValue);
        Assert.Equal(new[] { 1, 2, 3 }, Helpers.BuildTypedValue(Attr(MAttrType.OrgUnit), "1, 2;3").ArrayIntValue);
        Assert.Equal(new[] { "x", "y" }, Helpers.BuildTypedValue(Attr(MAttrType.Array), "x; y").ArrayValue);
    }

    [Fact]
    public void BuildTypedValue_StringKinds_KeepStrValue()
    {
        Assert.Equal("abc", Helpers.BuildTypedValue(Attr(MAttrType.String), "abc").StrValue);
        Assert.Equal("N-1", Helpers.BuildTypedValue(Attr(MAttrType.Numerator), "N-1").StrValue);
    }

    [Theory]
    [InlineData(MAttrType.Integer, "abc")]
    [InlineData(MAttrType.Integer, "99999999999999999999")]
    [InlineData(MAttrType.Double, "1,5x")]
    [InlineData(MAttrType.DateTime, "не дата")]
    [InlineData(MAttrType.UserState, "not-a-guid")]
    [InlineData(MAttrType.OrgUnit, "1,a")]
    public void BuildTypedValue_BadValue_ThrowsArgumentException(MAttrType type, string value)
    {
        var ex = Assert.Throws<ArgumentException>(() => Helpers.BuildTypedValue(Attr(type), value));
        Assert.Contains("'a'", ex.Message);
    }

    [Fact]
    public void BuildTypedValue_ByteArray_IsRejected()
        => Assert.Throws<ArgumentException>(() => Helpers.BuildTypedValue(Attr(MAttrType.ByteArray), "00"));

    private sealed class FakeFileApi : IFileArchiveApi
    {
        public Func<long, byte[]?> Chunk = _ => null;
        public byte[] GetFileChunk(Guid id, long pos, int count) => Chunk(pos)!;
        public void PutFileChunk(Guid id, byte[] buffer, long pos) => throw new NotSupportedException();
        public void PutFileChunk(Guid id, byte[] buffer, long pos, bool isLast) => throw new NotSupportedException();
        public long GetFilePosition(Guid id) => throw new NotSupportedException();
        [Obsolete]
        public void PutFileInArchive(DFileBody fileBody) => throw new NotSupportedException();
        public (Guid ListId, int FileCount) PrepareFileList() => throw new NotSupportedException();
        public IEnumerable<(Guid fileId, Guid objectId)> EnumerateFileList(Guid listId, int offset, int count) => throw new NotSupportedException();
        public void CloseFileList(Guid listId) => throw new NotSupportedException();
    }

    [Fact]
    public void MeasureArchiveFile_SumsChunks()
    {
        const int chunk = 1024 * 1024;
        var api = new FakeFileApi
        {
            Chunk = pos => pos switch
            {
                0 => new byte[chunk],
                chunk => new byte[chunk],
                _ => new byte[100]
            }
        };
        Assert.Equal(2L * chunk + 100, Helpers.MeasureArchiveFile(api, Guid.NewGuid()));
    }

    [Fact]
    public void MeasureArchiveFile_EmptyOrMissing_ReturnsZero()
    {
        Assert.Equal(0, Helpers.MeasureArchiveFile(new FakeFileApi(), Guid.NewGuid()));
        Assert.Equal(0, Helpers.MeasureArchiveFile(new FakeFileApi { Chunk = _ => Array.Empty<byte>() }, Guid.NewGuid()));
    }
}
