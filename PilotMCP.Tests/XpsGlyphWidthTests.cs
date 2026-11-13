using System.IO.Compression;
using System.Text;
using SkiaSharp;

namespace PilotMCP.Tests;

/// <summary>Ширина совпадения по натуральному advance последнего глифа из встроенного шрифта (без сервера).</summary>
public class XpsGlyphWidthTests : IDisposable
{
    private const string FontGuid = "0C8E9A6D-1B2F-4A3C-9D5E-7F8091A2B3C4";
    private static readonly string ArialPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "arial.ttf");
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"xps-glyph-{Guid.NewGuid():N}.xps");

    public void Dispose() => File.Delete(_path);

    /// <summary>Пакет с одной страницей; fontPart/fontBytes — встроенный шрифт (null — шрифта в пакете нет).</summary>
    private void WritePackage(string glyphs, string? fontPart, byte[]? fontBytes)
    {
        using var zip = ZipFile.Open(_path, ZipArchiveMode.Create);
        using (var w = new StreamWriter(zip.CreateEntry("Documents/1/Pages/1.fpage").Open(), Encoding.UTF8))
            w.Write("<FixedPage xmlns=\"http://schemas.microsoft.com/xps/2005/06\" Width=\"800\" Height=\"1000\">" + glyphs + "</FixedPage>");
        if (fontPart != null)
            using (var s = zip.CreateEntry(fontPart).Open()) s.Write(fontBytes!);
    }

    private static string Glyphs(string fontUri, string text, string indices) =>
        $"<Glyphs OriginX=\"10\" OriginY=\"100\" FontRenderingEmSize=\"10\" FontUri=\"{fontUri}\" UnicodeString=\"{text}\" Indices=\"{indices}\"/>";

    private static byte[] ObfuscatedArial()
    {
        var data = File.ReadAllBytes(ArialPath);
        XpsTextExtractor.Deobfuscate(data, FontGuid); // симметрична
        return data;
    }

    private static (double A, double B) ArialWidths(string chars)
    {
        using var tf = SKTypeface.FromFile(ArialPath);
        using var font = new SKFont(tf, 10) { Subpixel = true, LinearMetrics = true, Hinting = SKFontHinting.None };
        var w = font.GetGlyphWidths(tf.GetGlyphs(chars));
        return (w[0], w[1]);
    }

    [Fact]
    public void FindText_LastGlyphWithGapAdvance_UsesNaturalWidth_Obfuscated_NoGlyphIndex()
    {
        // Индексов глифов нет (",adv") — шрифт выбирается по cmap; у "B" advance 8 em включает зазор колонки
        WritePackage(Glyphs($"/Resources/Fonts/{FontGuid}.odttf", "AB", ",100;,800"),
            $"Resources/Fonts/{FontGuid}.odttf", ObfuscatedArial());
        var (_, natB) = ArialWidths("AB");

        var hit = Assert.Single(XpsTextExtractor.FindText(_path, "AB"));

        Assert.Equal(10 + natB, hit.Width, 3);
        Assert.InRange(natB, 6, 7);          // реальный advance Arial "B" ~0.667 em
        Assert.Equal(10, hit.X, 6);
    }

    [Fact]
    public void FindText_ExplicitGlyphIndices_PlainTtf_RelativeFontUri_InnerAdvancesStay()
    {
        using var tf = SKTypeface.FromFile(ArialPath);
        var g = tf.GetGlyphs("ABC");
        var (natA, natB) = ArialWidths("AB");
        WritePackage(Glyphs("../../../Resources/Fonts/arial.ttf", "ABC", $"{g[0]},200;{g[1]},300;{g[2]},900"),
            "Resources/Fonts/arial.ttf", File.ReadAllBytes(ArialPath));

        var ab = Assert.Single(XpsTextExtractor.FindText(_path, "AB"));
        var abc = Assert.Single(XpsTextExtractor.FindText(_path, "ABC"));

        Assert.Equal(20 + natB, ab.Width, 3);       // A: из Indices (2 em), последний B: натуральный (< 3 em)
        Assert.Equal(20 + 30 + ArialWidths("CA").A, abc.Width, 3);
        Assert.True(natA > 0);
    }

    [Fact]
    public void FindText_NaturalWidthLargerThanAdvance_KeepsIndicesAdvance()
    {
        WritePackage(Glyphs($"/Resources/Fonts/{FontGuid}.odttf", "AB", ",100;,10"),
            $"Resources/Fonts/{FontGuid}.odttf", ObfuscatedArial());

        var hit = Assert.Single(XpsTextExtractor.FindText(_path, "AB"));

        Assert.Equal(11, hit.Width, 6); // 1 em + 0.1 em: натуральная ширина шире — не расширяем
    }

    [Fact]
    public void FindText_FontMissingInPackage_KeepsIndicesWidthAndReportsToStderr()
    {
        WritePackage(Glyphs("/Resources/Fonts/missing.ttf", "AB", ",100;,800"), null, null);
        var prev = Console.Error;
        var err = new StringWriter();
        Console.SetError(err);
        try
        {
            var hit = Assert.Single(XpsTextExtractor.FindText(_path, "AB"));
            Assert.Equal(90, hit.Width, 6);
        }
        finally { Console.SetError(prev); }

        Assert.Contains("missing.ttf", err.ToString());
    }

    [Fact]
    public void FindText_AcrossRuns_LastRunUsesNaturalWidth()
    {
        var fontUri = $"/Resources/Fonts/{FontGuid}.odttf";
        var tail = $"<Glyphs OriginX=\"20\" OriginY=\"100\" FontRenderingEmSize=\"10\" FontUri=\"{fontUri}\" UnicodeString=\"B\" Indices=\",800\"/>";
        WritePackage(Glyphs(fontUri, "A", ",100") + tail, $"Resources/Fonts/{FontGuid}.odttf", ObfuscatedArial());
        var (_, natB) = ArialWidths("AB");

        var hit = Assert.Single(XpsTextExtractor.FindText(_path, "AB"));

        Assert.Equal(10 + natB, hit.Width, 3);
    }

    [Fact]
    public void Deobfuscate_RoundTrip_ChangesOnlyFirst32Bytes_AndSkiaAcceptsResult()
    {
        var original = File.ReadAllBytes(ArialPath);
        var data = (byte[])original.Clone();

        XpsTextExtractor.Deobfuscate(data, FontGuid);

        Assert.NotEqual(original[..32], data[..32]);
        Assert.Equal(original[32..], data[32..]);
        using (var broken = SKData.CreateCopy(data)) Assert.Null(SKTypeface.FromData(broken));
        XpsTextExtractor.Deobfuscate(data, FontGuid);
        Assert.Equal(original, data);
        using var ok = SKData.CreateCopy(data);
        using var tf = SKTypeface.FromData(ok);
        Assert.NotNull(tf);
    }

    [Fact]
    public void Deobfuscate_KeyIsGuidHexInStringOrderReversed()
    {
        var data = new byte[32];
        XpsTextExtractor.Deobfuscate(data, "00112233-4455-6677-8899-AABBCCDDEEFF");

        // ключ в порядке строки 00 11 .. FF; применяется с конца: data[0] = key[15] = FF
        Assert.Equal(0xFF, data[0]);
        Assert.Equal(0x00, data[15]);
        Assert.Equal(data[..16], data[16..]);
    }

    [Fact]
    public void Deobfuscate_NotGuidName_Throws() =>
        Assert.Throws<InvalidDataException>(() => XpsTextExtractor.Deobfuscate(new byte[32], "not-a-guid"));
}
