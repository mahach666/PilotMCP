using Ascon.Pilot.DataClasses.Markups;

namespace PilotMCP.Tests;

/// <summary>Чистые unit-тесты MarkupBuilder (без сервера): ожидаемые числа посчитаны вручную.</summary>
public class MarkupBuilderTests
{
    // Страница высотой 1000 DIP, 96 dpi: 1 пиксель = 1 DIP = 0.75 pt; Y в Markup направлена вверх.
    private const double PageH = 1000;

    private static ShapeSpec Spec(string shape, Action<ShapeSpec>? tune = null)
    {
        var s = new ShapeSpec { Shape = shape };
        tune?.Invoke(s);
        return s;
    }

    [Fact]
    public void Rectangle_ConvertsPixelsToPointsWithFlippedY()
    {
        var m = Assert.IsType<RectangleMarkup>(MarkupBuilder.Build(7, Spec("rectangle", s =>
        {
            s.X = 96; s.Y = 96; s.Width = 96; s.Height = 48; s.Page = 2; s.Color = "red";
        }), 96, PageH));

        Assert.Equal(7, m.Id);
        Assert.Equal(3, m.Page); // страница с 0 → Markup.Page с 1
        Assert.Equal(72f, m.Bounds.X);
        Assert.Equal(678f, m.Bounds.Y);       // (1000 - 96) * 0.75
        Assert.Equal(72f, m.Bounds.Width);
        Assert.Equal(-36f, m.Bounds.Height);  // клиент хранит прямоугольник с отрицательной высотой
        Assert.Equal(0xFF0000, m.Style.ColorRgb);
    }

    [Fact]
    public void Rectangle_ScalesByDpi()
    {
        // 192 dpi: 192 px = 96 DIP = 72 pt
        var m = Assert.IsType<RectangleMarkup>(MarkupBuilder.Build(1, Spec("rectangle", s =>
        {
            s.X = 192; s.Y = 192; s.Width = 192; s.Height = 96;
        }), 192, PageH));
        Assert.Equal(72f, m.Bounds.X);
        Assert.Equal(678f, m.Bounds.Y);
        Assert.Equal(72f, m.Bounds.Width);
        Assert.Equal(-36f, m.Bounds.Height);
    }

    [Fact]
    public void BlackColor_IsStoredWithAlpha()
    {
        var m = MarkupBuilder.Build(1, Spec("point", s => s.Color = "black"), 96, PageH);
        Assert.Equal(unchecked((int)0xFF000000), m.Style.ColorRgb);
    }

    [Theory]
    [InlineData("#FF0000", 0xFF0000)]
    [InlineData("#00ff00", 0x00FF00)]
    [InlineData("#80FF0000", unchecked((int)0x80FF0000))]
    [InlineData("#00FF0000", 0)] // нулевая альфа: клиент считает прозрачным только 0
    public void ParseColor_AcceptsHex(string color, int expected) => Assert.Equal(expected, MarkupBuilder.ParseColor(color));

    [Theory]
    [InlineData("notacolor")]
    [InlineData("")]
    [InlineData("#12")]
    [InlineData("#GGGGGG")]
    [InlineData("#FF00")]
    public void ParseColor_RejectsUnknown(string color) => Assert.Throws<ArgumentException>(() => MarkupBuilder.ParseColor(color));

    [Theory]
    [InlineData(0, "transparent")]
    [InlineData(0xFF0000, "#FF0000")]
    [InlineData(unchecked((int)0xFF000000), "#FF000000")]
    [InlineData(unchecked((int)0x80FF0000), "#80FF0000")]
    public void FormatColor_Formats(int color, string expected) => Assert.Equal(expected, MarkupBuilder.FormatColor(color));

    [Fact]
    public void TextDecoration_QuadOrderIsTopLeftTopRightBottomRightBottomLeft()
    {
        var m = Assert.IsType<TextDecorationMarkup>(
            MarkupBuilder.TextDecoration(1, 0, 96, 100, 200, 20, "highlight", "black", PageH));

        Assert.Equal(1, m.Page);
        Assert.Equal(unchecked((int)0xFF000000), m.Style.ColorRgb);
        var q = m.QuadPoints.ToArray();
        Assert.Equal(4, q.Length);
        Assert.Equal((72f, 675f), (q[0].X, q[0].Y));   // TL: (1000 - 100) * 0.75
        Assert.Equal((222f, 675f), (q[1].X, q[1].Y));  // TR
        Assert.Equal((222f, 660f), (q[2].X, q[2].Y));  // BR: (1000 - 120) * 0.75
        Assert.Equal((72f, 660f), (q[3].X, q[3].Y));   // BL
    }

    [Theory]
    [InlineData("bold")]
    [InlineData("squiggly")] // список замечаний клиента 26.20 падает на Squiggly
    public void TextDecoration_UnsupportedStyle_Throws(string style)
        => Assert.Throws<ArgumentException>(() => MarkupBuilder.TextDecoration(1, 0, 0, 0, 10, 10, style, "red", PageH));

    [Fact]
    public void Arrow_HasOpenArrowAtTip()
    {
        var m = Assert.IsType<LineMarkup>(MarkupBuilder.Build(1, Spec("arrow", s =>
        {
            s.X = 96; s.Y = 96; s.Width = 96; s.Height = 0;
        }), 96, PageH));
        Assert.Equal(MarkupLineEndings.OpenArrow, m.EndEnding);
        Assert.Equal(MarkupLineEndings.None, m.StartEnding);
    }

    [Theory]
    [InlineData("freetext")]
    [InlineData("hexagon")]
    [InlineData("")]
    public void UnsupportedShape_Throws(string shape)
        => Assert.Throws<ArgumentException>(() => MarkupBuilder.Build(1, Spec(shape), 96, PageH));

    [Theory]
    [InlineData("closedarrow")]
    [InlineData("circle")]
    [InlineData("square")]
    [InlineData("diamond")]
    public void UnsupportedEnding_Throws(string ending)
        => Assert.Throws<ArgumentException>(() => MarkupBuilder.Build(1, Spec("arrow", s => s.EndEnding = ending), 96, PageH));

    [Fact]
    public void NullStrings_BecomeDefaultsOrArgumentException()
    {
        var m = MarkupBuilder.Build(1, Spec("rectangle", s =>
        {
            s.Color = null!; s.Points = null!; s.StartEnding = null!; s.EndEnding = null!;
        }), 96, PageH);
        Assert.Equal(0xFF0000, m.Style.ColorRgb); // красный по умолчанию

        Assert.Throws<ArgumentException>(() => MarkupBuilder.Build(1, new ShapeSpec { Shape = null! }, 96, PageH));
        Assert.Throws<ArgumentException>(() => MarkupBuilder.Build(1, Spec("polygon", s => s.Points = null!), 96, PageH));
    }

    [Theory]
    [InlineData("rectangle")]
    [InlineData("ellipse")]
    [InlineData("triangle")]
    public void NonPositiveSize_Throws(string shape)
    {
        Assert.Throws<ArgumentException>(() => MarkupBuilder.Build(1, Spec(shape, s => s.Width = 0), 96, PageH));
        Assert.Throws<ArgumentException>(() => MarkupBuilder.Build(1, Spec(shape, s => s.Height = -5), 96, PageH));
    }

    [Fact]
    public void NonFiniteNumbers_Throw()
    {
        Assert.Throws<ArgumentException>(() => MarkupBuilder.Build(1, Spec("point", s => s.X = double.NaN), 96, PageH));
        Assert.Throws<ArgumentException>(() => MarkupBuilder.Build(1, Spec("point", s => s.StrokeWidth = double.PositiveInfinity), 96, PageH));
        Assert.Throws<ArgumentException>(() => MarkupBuilder.Build(1, Spec("polyline", s => s.Points = "1,1;NaN,2"), 96, PageH));
    }
}
