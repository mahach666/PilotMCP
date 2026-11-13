using System.IO.Compression;
using System.Text;

namespace PilotMCP.Tests;

/// <summary>Порядок страниц XPS берётся из FixedDocument, а не из имён файлов (без сервера).</summary>
public class XpsPageOrderTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"xps-order-{Guid.NewGuid():N}.xps");

    public void Dispose() => File.Delete(_path);

    private void WriteXps(params (string Name, string Content)[] parts)
    {
        using var zip = ZipFile.Open(_path, ZipArchiveMode.Create);
        foreach (var (name, content) in parts)
        {
            using var w = new StreamWriter(zip.CreateEntry(name).Open(), Encoding.UTF8);
            w.Write(content);
        }
    }

    private static string Page(int w, int h) =>
        $"<FixedPage xmlns=\"http://schemas.microsoft.com/xps/2005/06\" Width=\"{w}\" Height=\"{h}\" />";

    private const string Rels =
        "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
        "<Relationship Type=\"http://schemas.microsoft.com/xps/2005/06/fixedrepresentation\" Target=\"/FixedDocSeq.fdseq\" Id=\"R0\" />" +
        "</Relationships>";

    private const string Seq =
        "<FixedDocumentSequence xmlns=\"http://schemas.microsoft.com/xps/2005/06\">" +
        "<DocumentReference Source=\"Documents/1/FixedDoc.fdoc\" /></FixedDocumentSequence>";

    [Fact]
    public void GetPageSizes_FollowsFixedDocumentOrder()
    {
        // Порядок в FixedDocument: 10, 2, 1 — не совпадает ни со строковой, ни с числовой сортировкой имён.
        WriteXps(
            ("_rels/.rels", Rels),
            ("FixedDocSeq.fdseq", Seq),
            ("Documents/1/FixedDoc.fdoc",
                "<FixedDocument xmlns=\"http://schemas.microsoft.com/xps/2005/06\">" +
                "<PageContent Source=\"Pages/10.fpage\" /><PageContent Source=\"Pages/2.fpage\" />" +
                "<PageContent Source=\"/Documents/1/Pages/1.fpage\" /></FixedDocument>"),
            ("Documents/1/Pages/1.fpage", Page(100, 101)),
            ("Documents/1/Pages/2.fpage", Page(200, 202)),
            ("Documents/1/Pages/10.fpage", Page(1000, 1010)));

        Assert.Equal(new[] { (1000.0, 1010.0), (200.0, 202.0), (100.0, 101.0) }, XpsTextExtractor.GetPageSizes(_path));
    }

    [Fact]
    public void GetPageSizes_JoinsInterleavedPiecesInIndexOrder()
    {
        var page = Page(300, 400);
        WriteXps(
            ("_rels/.rels", Rels),
            ("FixedDocSeq.fdseq", Seq),
            ("Documents/1/FixedDoc.fdoc",
                "<FixedDocument xmlns=\"http://schemas.microsoft.com/xps/2005/06\"><PageContent Source=\"Pages/1.fpage\" /></FixedDocument>"),
            ("Documents/1/Pages/1.fpage/[10].last.piece", page[20..]),
            ("Documents/1/Pages/1.fpage/[2].piece", page[..20]));

        Assert.Equal(new[] { (300.0, 400.0) }, XpsTextExtractor.GetPageSizes(_path));
    }

    [Fact]
    public void GetPageSizes_ZipBombPage_ThrowsInsteadOfExhaustingMemory()
    {
        WriteXps(
            ("_rels/.rels", Rels),
            ("FixedDocSeq.fdseq", Seq),
            ("Documents/1/FixedDoc.fdoc",
                "<FixedDocument xmlns=\"http://schemas.microsoft.com/xps/2005/06\"><PageContent Source=\"Pages/1.fpage\" /></FixedDocument>"));
        using (var zip = ZipFile.Open(_path, ZipArchiveMode.Update))
        using (var s = zip.CreateEntry("Documents/1/Pages/1.fpage", CompressionLevel.Optimal).Open())
        {
            // ~65 МБ пробелов сжимаются в десятки КБ: часть больше лимита 64 МБ после распаковки
            var chunk = new byte[1 << 20];
            Array.Fill(chunk, (byte)' ');
            for (var i = 0; i < 65; i++) s.Write(chunk);
        }

        var ex = Assert.Throws<InvalidDataException>(() => XpsTextExtractor.GetPageSizes(_path));
        Assert.Contains("превышает допустимый размер", ex.Message);
    }

    [Fact]
    public void GetPageSizes_TooManyPageReferences_Throws()
    {
        var refs = string.Concat(Enumerable.Repeat("<PageContent Source=\"Pages/1.fpage\" />", 10_001));
        WriteXps(
            ("_rels/.rels", Rels),
            ("FixedDocSeq.fdseq", Seq),
            ("Documents/1/FixedDoc.fdoc", $"<FixedDocument xmlns=\"http://schemas.microsoft.com/xps/2005/06\">{refs}</FixedDocument>"),
            ("Documents/1/Pages/1.fpage", Page(100, 100)));

        var ex = Assert.Throws<InvalidDataException>(() => XpsTextExtractor.GetPageSizes(_path));
        Assert.Contains("страниц", ex.Message);
    }

    [Fact]
    public void GetPageSizes_MissingSize_Throws()
    {
        WriteXps(
            ("_rels/.rels", Rels),
            ("FixedDocSeq.fdseq", Seq),
            ("Documents/1/FixedDoc.fdoc",
                "<FixedDocument xmlns=\"http://schemas.microsoft.com/xps/2005/06\"><PageContent Source=\"Pages/1.fpage\" /></FixedDocument>"),
            ("Documents/1/Pages/1.fpage", "<FixedPage xmlns=\"http://schemas.microsoft.com/xps/2005/06\" />"));

        Assert.Throws<InvalidDataException>(() => XpsTextExtractor.GetPageSizes(_path));
    }
}
