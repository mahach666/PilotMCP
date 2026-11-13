using Ascon.Pilot.DataClasses;
using Ascon.Pilot.Server.Api.Contracts;
using Ascon.Pilot.Transport;
using PilotMCP;

namespace PilotMCP.Tests;

/// <summary>Чистые unit-тесты инфраструктуры (FormatDValue, ParseAttributes, DownloadToFile, ReconnectingProxy) без сервера.</summary>
public class CoreInfrastructureTests
{
    // ---- FormatDValue ----

    [Fact]
    public void FormatDValue_ShowsEveryValueKind()
    {
        Assert.Equal("5", Helpers.FormatDValue(new DValue { IntValue = 5 }));
        Assert.Equal("0", Helpers.FormatDValue(new DValue { IntValue = 0 }));
        Assert.Equal("2.5", Helpers.FormatDValue(new DValue { DoubleValue = 2.5 }));
        Assert.Equal("3", Helpers.FormatDValue(new DValue { DecimalValue = 3m }));
        Assert.Equal("a, b", Helpers.FormatDValue(new DValue { ArrayValue = new[] { "a", "b" } }));
        Assert.Equal("1, 2", Helpers.FormatDValue(new DValue { ArrayIntValue = new[] { 1, 2 } }));
        var g = Guid.NewGuid();
        Assert.Equal(g.ToString(), Helpers.FormatDValue(new DValue { GuidValue = g }));
        Assert.Equal("text", Helpers.FormatDValue(new DValue { StrValue = "text" }));
        Assert.Equal("", Helpers.FormatDValue(new DValue()));
        Assert.Equal("", Helpers.FormatDValue(null));
    }

    [Fact]
    public void FormatDValue_DoubleIsCultureInvariant()
    {
        var old = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("ru-RU");
            Assert.Equal("1.5", Helpers.FormatDValue(new DValue { DoubleValue = 1.5 }));
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = old; }
    }

    // ---- ParseAttributes ----

    [Fact]
    public void ParseAttributes_MapsKindsAndSkipsNull()
    {
        var d = Helpers.ParseAttributes("""{"s":"x","i":7,"f":1.5,"b":true,"n":null,"ints":[1,2],"strs":["a","b"]}""")!;
        Assert.Equal("x", d["s"].StrValue);
        Assert.Equal(7, d["i"].IntValue);
        Assert.Equal(1.5, d["f"].DoubleValue);
        Assert.Equal(1, d["b"].IntValue);
        Assert.False(d.ContainsKey("n"));
        Assert.Equal(new[] { 1, 2 }, d["ints"].ArrayIntValue);
        Assert.Equal(new[] { "a", "b" }, d["strs"].ArrayValue);
    }

    [Fact]
    public void ParseAttributes_EmptyInput_ReturnsNull()
    {
        Assert.Null(Helpers.ParseAttributes(""));
        Assert.Null(Helpers.ParseAttributes("  "));
    }

    [Theory]
    [InlineData("{bad json")]
    [InlineData("[1,2]")]
    [InlineData("""{"a":{"x":1}}""")]
    [InlineData("""{"a":[1,"x"]}""")]
    [InlineData("""{"a":[]}""")]
    public void ParseAttributes_InvalidInput_Throws(string json)
        => Assert.Throws<ArgumentException>(() => Helpers.ParseAttributes(json));

    // ---- DownloadToFile ----

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

    private static string TempFile() => Path.Combine(Path.GetTempPath(), "pilotmcp-test-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void DownloadToFile_Success_ReplacesTargetAndLeavesNoPart()
    {
        var path = TempFile();
        try
        {
            File.WriteAllText(path, "old");
            var api = new FakeFileApi { Chunk = pos => pos == 0 ? new byte[] { 1, 2, 3 } : null };
            Assert.Equal(3, Helpers.DownloadToFile(api, Guid.NewGuid(), path));
            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(path));
            Assert.False(File.Exists(path + ".part"));
        }
        finally { File.Delete(path); File.Delete(path + ".part"); }
    }

    [Fact]
    public void DownloadToFile_ServerFails_KeepsExistingFile()
    {
        var path = TempFile();
        try
        {
            File.WriteAllText(path, "PRECIOUS");
            var api = new FakeFileApi { Chunk = _ => throw new InvalidOperationException("boom") };
            Assert.Throws<InvalidOperationException>(() => Helpers.DownloadToFile(api, Guid.NewGuid(), path));
            Assert.Equal("PRECIOUS", File.ReadAllText(path));
            Assert.False(File.Exists(path + ".part"));
        }
        finally { File.Delete(path); File.Delete(path + ".part"); }
    }

    [Fact]
    public void DownloadToFile_EmptyContent_IsErrorAndKeepsExistingFile()
    {
        var path = TempFile();
        try
        {
            File.WriteAllText(path, "PRECIOUS");
            var api = new FakeFileApi { Chunk = _ => Array.Empty<byte>() };
            Assert.Throws<InvalidOperationException>(() => Helpers.DownloadToFile(api, Guid.NewGuid(), path));
            Assert.Equal("PRECIOUS", File.ReadAllText(path));
        }
        finally { File.Delete(path); File.Delete(path + ".part"); }
    }

    // ---- ReconnectingProxy ----

    public interface IFakeApi
    {
        int GetValue();
        void SendMessage();
        Task<int> GetValueAsync();
        Task SendMessageAsync();
    }

    private sealed class FakeApi : IFakeApi
    {
        public Func<int, Exception?> FailOnCall = _ => null; // по номеру вызова (с 1)
        public int Calls;

        private void Step()
        {
            var ex = FailOnCall(++Calls);
            if (ex != null) throw ex;
        }

        public int GetValue() { Step(); return 42; }
        public void SendMessage() => Step();
        public async Task<int> GetValueAsync() { await Task.Yield(); Step(); return 42; }
        public async Task SendMessageAsync() { await Task.Yield(); Step(); }
    }

    private static (IFakeApi Proxy, FakeApi Target, Func<int> Restores) MakeProxy(Func<int, Exception?> fail)
    {
        var target = new FakeApi { FailOnCall = fail };
        var restores = 0;
        var proxy = ReconnectingProxy<IFakeApi>.Create(target, () => target, () => restores++, () => false);
        return (proxy, target, () => restores);
    }

    [Fact]
    public void Proxy_ReadAfterTransportError_ReconnectsAndRetries()
    {
        var (proxy, target, restores) = MakeProxy(n => n == 1 ? new TransportException("Canceled by transport") : null);
        Assert.Equal(42, proxy.GetValue());
        Assert.Equal(2, target.Calls);
        Assert.Equal(1, restores());
    }

    [Fact]
    public void Proxy_WriteAfterTimeout_ReconnectsButIsNotReplayed()
    {
        var (proxy, target, restores) = MakeProxy(_ => new TimeoutException("slow"));
        Assert.Throws<TimeoutException>(() => proxy.SendMessage());
        Assert.Equal(1, target.Calls);
        Assert.Equal(1, restores());
    }

    [Fact]
    public async Task Proxy_AsyncWriteAfterTransportError_IsNotReplayed()
    {
        var (proxy, target, _) = MakeProxy(_ => new TransportException("Canceled by transport"));
        await Assert.ThrowsAsync<TransportException>(() => proxy.SendMessageAsync());
        Assert.Equal(1, target.Calls);
    }

    [Fact]
    public async Task Proxy_AsyncReadAfterTransportError_Retries()
    {
        var (proxy, target, _) = MakeProxy(n => n == 1 ? new TransportException("Transport closed") : null);
        Assert.Equal(42, await proxy.GetValueAsync());
        Assert.Equal(2, target.Calls);
    }

    [Fact]
    public void Proxy_WriteOnDisposedTarget_IsReplayed()
    {
        var (proxy, target, _) = MakeProxy(n => n == 1 ? new ObjectDisposedException("TransportClient") : null);
        proxy.SendMessage();
        Assert.Equal(2, target.Calls);
    }

    [Fact]
    public void Proxy_NonTransportError_IsUnwrappedFromTargetInvocationException()
    {
        var (proxy, target, restores) = MakeProxy(_ => new InvalidOperationException("access denied"));
        var ex = Assert.Throws<InvalidOperationException>(() => proxy.GetValue());
        Assert.Equal("access denied", ex.Message);
        Assert.Equal(1, target.Calls);
        Assert.Equal(0, restores());
    }
}
