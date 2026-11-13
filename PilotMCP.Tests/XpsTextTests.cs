using System.IO.Compression;
using System.Text;

namespace PilotMCP.Tests;

/// <summary>Разбор Glyphs и поиск текста в XPS (без сервера).</summary>
public class XpsTextTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"xps-text-{Guid.NewGuid():N}.xps");

    public void Dispose() => File.Delete(_path);

    // Пакет без FixedDocumentSequence: страница берётся по имени .fpage
    private void WritePage(string body)
    {
        using var zip = ZipFile.Open(_path, ZipArchiveMode.Create);
        using var w = new StreamWriter(zip.CreateEntry("Documents/1/Pages/1.fpage").Open(), Encoding.UTF8);
        w.Write("<FixedPage xmlns=\"http://schemas.microsoft.com/xps/2005/06\" Width=\"800\" Height=\"1000\">" + body + "</FixedPage>");
    }

    private static string Glyphs(string text, double x, double y, string extra = "", string children = "") =>
        FormattableString.Invariant($"<Glyphs OriginX=\"{x}\" OriginY=\"{y}\" FontRenderingEmSize=\"10\" UnicodeString=\"{text}\" {extra}>{children}</Glyphs>");

    [Fact]
    public void FindText_ReturnsEveryMatchInsideOneRun()
    {
        WritePage(Glyphs("foo bar foo", 10, 100)); // без Indices: 5 единиц на символ

        var found = XpsTextExtractor.FindText(_path, "foo");

        Assert.Equal(2, found.Count);
        Assert.Equal(10, found[0].X, 6);
        Assert.Equal(50, found[1].X, 6);
        Assert.All(found, f => Assert.Equal(15, f.Width, 6));
        Assert.All(found, f => Assert.Equal(90, f.Y, 6));
    }

    [Fact]
    public void FindText_EmptyText_Throws()
    {
        WritePage(Glyphs("foo", 10, 100));
        Assert.Throws<ArgumentException>(() => XpsTextExtractor.FindText(_path, ""));
    }

    [Fact]
    public void FindText_AcrossRuns_KeepsTopYAndMeasuresWidthByAdvances()
    {
        // Базовые линии 102.4 и 102.6 раньше попадали в разные корзины Round(Y/5)*5
        WritePage(Glyphs("ab", 10, 102.4) + Glyphs("cd", 20, 102.6));

        var found = XpsTextExtractor.FindText(_path, "bc");

        var hit = Assert.Single(found);
        Assert.Equal("bc", hit.Text);
        Assert.Equal(15, hit.X, 6);
        Assert.Equal(10, hit.Width, 6);
        Assert.Equal(92.4, hit.Y, 6); // верх текста, без повторного вычитания размера шрифта
    }

    [Fact]
    public void ExtractText_StripsEscapePrefix()
    {
        WritePage(Glyphs("{}foo", 10, 100));
        Assert.Equal("foo", Assert.Single(XpsTextExtractor.ExtractText(_path)).Text);
    }

    [Fact]
    public void ExtractText_ClusterMapSharesAdvanceBetweenCharacters()
    {
        // "fi" — один глиф шириной 30 (300/100 * 10), затем "x" шириной 10
        WritePage(Glyphs("fix", 10, 100, "Indices=\"(2:1)5,300;3,100\""));

        var block = Assert.Single(XpsTextExtractor.ExtractText(_path));

        Assert.Equal(new[] { 15.0, 15.0, 10.0 }, block.Advances);
        var hit = Assert.Single(XpsTextExtractor.FindText(_path, "x"));
        Assert.Equal(40, hit.X, 6);
        Assert.Equal(10, hit.Width, 6);
    }

    [Fact]
    public void ExtractText_ReadsRenderTransformPropertyElement()
    {
        WritePage(Glyphs("foo", 10, 100, children:
            "<Glyphs.RenderTransform><MatrixTransform Matrix=\"1,0,0,1,100,0\" /></Glyphs.RenderTransform>"));

        Assert.Equal(110, Assert.Single(XpsTextExtractor.ExtractText(_path)).X, 6);
    }

    [Fact]
    public void GetPageCount_CountsPages()
    {
        WritePage(Glyphs("foo", 10, 100));
        Assert.Equal(1, XpsTextExtractor.GetPageCount(_path));
    }
}
