using Ascon.Pilot.DataClasses.Markups;
using Ascon.Pilot.DataClasses.Markups.Primitives;
using System.Globalization;
using System.Text.RegularExpressions;

namespace PilotMCP;

/// <summary>Описание одной фигуры замечания. Координаты и размеры — в пикселях изображения GetDocumentPageImage.</summary>
public class ShapeSpec
{
    public string Shape { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; } = 100;
    public double Height { get; set; } = 60;
    public int Page { get; set; }
    public string Color { get; set; } = "red";
    public double StrokeWidth { get; set; } = 2;
    public string Points { get; set; } = "";
    public bool Cloud { get; set; }
    public string StartEnding { get; set; } = "";
    public string EndEnding { get; set; } = "";
}

/// <summary>
/// Строит Markup-объекты Pilot (формат замечаний клиента 26.20+) из фигур в пикселях верхне-левой системы координат.
/// Единицы Markup — пункты PDF (DIP * 72/96), ось Y направлена вверх от низа страницы, Markup.Page считается с 1.
/// </summary>
public static class MarkupBuilder
{
    private const double PtPerDip = 72.0 / 96.0;
    // Squiggly намеренно нет: список замечаний клиента 26.20 (MarkupListItem.ToCommandType) падает на нём
    // с "Unknown MarkupSubtype: Squiggly"; интерфейс клиента сам создаёт только эти три вида.
    public static readonly Dictionary<string, MarkupTextDecorationKind> TextDecorationKinds = new()
    {
        ["highlight"] = MarkupTextDecorationKind.Highlight,
        ["underline"] = MarkupTextDecorationKind.Underline,
        ["strikeout"] = MarkupTextDecorationKind.StrikeOut
    };

    private const float StickerSize = 24; // размер стикера-точки, как в AnnotationsHelper.CreateMarkupAnnotation клиента

    /// <summary>Фигура → Markup. Некорректные данные — ArgumentException.</summary>
    public static Markup Build(int id, ShapeSpec s, double dpi, double pageHeightDip)
    {
        Validate(s);
        var k = 96.0 / dpi; // пиксели → DIP
        float X(double px) => (float)(px * k * PtPerDip);
        float Y(double px) => (float)((pageHeightDip - px * k) * PtPerDip); // сверху вниз → снизу вверх
        float L(double px) => (float)(px * k * PtPerDip);
        MarkupPoint P(double px, double py) => new(X(px), Y(py));

        var shape = s.Shape.Trim().ToLowerInvariant();
        var page = s.Page + 1;
        var style = BuildStyle(s);

        if (s.Cloud && shape is not ("polyline" or "polygon"))
            throw new ArgumentException("cloud применим только к polyline и polygon.");
        var hasEndings = s.StartEnding != "" || s.EndEnding != "";
        if (hasEndings && shape is not ("arrow" or "line"))
            throw new ArgumentException("startEnding/endEnding применимы только к arrow и line.");

        var border = s.Cloud ? BorderEffect.Cloud : BorderEffect.None;

        // Прямоугольник клиент хранит как Y = верхний край (ось вверх) и ОТРИЦАТЕЛЬНУЮ высоту:
        // RectangleMarkupGeometryConverter.ToCompactMarkup (BottomRight.Y после переворота меньше TopLeft.Y).
        MarkupRect Rect() => new(X(s.X), Y(s.Y), L(s.Width), -L(s.Height));

        if (shape is "rectangle" or "rect" or "ellipse" or "circle" or "triangle" && (s.Width <= 0 || s.Height <= 0))
            throw new ArgumentException($"Для {shape} width и height должны быть больше 0.");

        switch (shape)
        {
            case "rectangle" or "rect":
                return new RectangleMarkup(id, page, Rect(), style);
            case "ellipse" or "circle": // (x, y) = центр; радиусы положительные
                return new EllipseMarkup(id, page, P(s.X, s.Y), L(s.Width) / 2, L(s.Height) / 2, style);
            case "triangle":
                return new PolygonMarkup(id, page, new[]
                {
                    P(s.X, s.Y + s.Height), P(s.X + s.Width / 2, s.Y), P(s.X + s.Width, s.Y + s.Height)
                }, style);
            case "arrow" or "line":
            {
                // (x, y) = остриё, хвост = (x+width, y+height). Клиент ставит OpenArrow на конец линии (End):
                // LineGeometryConverter.ToCompactMarkup.
                var start = ParseEnding(s.StartEnding, MarkupLineEndings.None);
                var end = ParseEnding(s.EndEnding, shape == "arrow" ? MarkupLineEndings.OpenArrow : MarkupLineEndings.None);
                return new LineMarkup(id, page, P(s.X + s.Width, s.Y + s.Height), P(s.X, s.Y), start, end, style);
            }
            case "polyline":
                return new PolylineMarkup(id, page, ParsePoints(s.Points, 2, P), style, border);
            case "polygon":
                return new PolygonMarkup(id, page, ParsePoints(s.Points, 3, P), style, border);
            case "ink":
                return new InkMarkup(id, page, ParsePoints(s.Points, 2, P), style);
            case "point" or "textnote":
                // Стикер: X, Y = левый верхний угол (ось вверх), размер 24x24 — как CreateNewMarkupForPoint клиента.
                return new TextMarkup(id, page, new MarkupRect(X(s.X), Y(s.Y), StickerSize, StickerSize), style);
            default:
                throw new ArgumentException(
                    $"Неизвестная фигура: {s.Shape}. Используйте: rectangle, ellipse, triangle, arrow, line, polyline, polygon, ink, point");
        }
    }

    /// <summary>
    /// Выделение текста: одна строка (четырёхугольник) в DIP, верхне-левая система координат.
    /// Вершины строго TL, TR, BR, BL в системе с осью вверх (у TL Y больше) — так их пишет клиент
    /// (GetSelectionQuads) и читает TextDecorationMarkupGeometryConverter.
    /// </summary>
    public static Markup TextDecoration(int id, int page, double xDip, double topDip, double widthDip, double heightDip,
        string kind, string color, double pageHeightDip)
    {
        if (!TextDecorationKinds.TryGetValue(kind.Trim().ToLowerInvariant(), out var decoration))
            throw new ArgumentException($"Неизвестный стиль: {kind}. Используйте: {string.Join(", ", TextDecorationKinds.Keys)}");
        MarkupPoint P(double x, double y) => new((float)(x * PtPerDip), (float)((pageHeightDip - y) * PtPerDip));
        var quad = new[]
        {
            P(xDip, topDip), P(xDip + widthDip, topDip), P(xDip + widthDip, topDip + heightDip), P(xDip, topDip + heightDip)
        };
        // Клиент создаёт выделение текста с LineStyle = Solid и толщиной 2 (AnnotationManager.CreateNewMarkupForText).
        var style = new MarkupStyle(ParseColor(color), 2, null, MarkupLineStyle.Solid);
        return new TextDecorationMarkup(id, page + 1, quad, decoration, style);
    }

    /// <summary>Ключевые точки фигуры в единицах Markup — по ним считается ограничивающий прямоугольник.</summary>
    public static IEnumerable<MarkupPoint> Outline(Markup m) => m switch
    {
        RectangleMarkup r => Corners(r.Bounds),
        FreeTextMarkup f => Corners(f.Bounds),
        TextMarkup t => Corners(t.Bounds),
        EllipseMarkup e => Corners(e.GetBounds()),
        LineMarkup l => new[] { l.Start, l.End },
        PolylineMarkup p => p.Points,
        PolygonMarkup p => p.Points,
        InkMarkup i => i.Points.Where(p => !float.IsNaN(p.X) && !float.IsNaN(p.Y)),
        TextDecorationMarkup d => d.QuadPoints,
        _ => Array.Empty<MarkupPoint>()
    };

    private static MarkupPoint[] Corners(MarkupRect r) => new MarkupPoint[] { new(r.X, r.Y), new(r.Right, r.Bottom) };

    /// <summary>
    /// Цвет → int для MarkupStyle. Непрозрачный цвет хранится как 0xRRGGBB (так делает клиент),
    /// кроме чёрного: 0 клиент читает как полностью прозрачный, поэтому чёрный — 0xFF000000.
    /// </summary>
    public static int ParseColor(string color)
    {
        var c = color.Trim();
        // ResolveColor молча подменяет неизвестное на красный; для Markup такое не допускаем:
        // имя допустимо, если ResolveColor вернул не красный-по-умолчанию (или это сам "red").
        var valid = Regex.IsMatch(c, "^#([0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})$")
            || (!c.StartsWith('#') && (c.Equals("red", StringComparison.OrdinalIgnoreCase)
                || AnnotationBuilder.ResolveColor(c) != "#FFFF0000"));
        if (!valid)
            throw new ArgumentException(
                $"Неизвестный цвет: '{color}'. Используйте имя (red, green, blue, yellow, orange, purple, pink, black, white, gray, cyan, magenta) или #RRGGBB / #AARRGGBB.");
        var argb = Convert.ToUInt32(AnnotationBuilder.ResolveColor(c)[1..], 16);
        // клиент (ColorHelper.FromRgb) читает 0x00RRGGBB как непрозрачный — полностью прозрачный только 0
        if ((argb >> 24) == 0) return 0;
        if ((argb >> 24) != 0xFF) return unchecked((int)argb);
        var rgb = (int)(argb & 0xFFFFFF);
        return rgb == 0 ? unchecked((int)0xFF000000) : rgb;
    }

    /// <summary>Цвет из Markup-стиля → строка: "transparent" для 0, #AARRGGBB при ненулевой альфе, иначе #RRGGBB.</summary>
    public static string FormatColor(int c)
        => c == 0 ? "transparent" : (c >>> 24) != 0 ? $"#{(uint)c:X8}" : $"#{c:X6}";

    // Заливки и стиль линии клиент не рисует, поэтому их нет: фон null, линия сплошная.
    private static MarkupStyle BuildStyle(ShapeSpec s)
    {
        if (s.StrokeWidth <= 0)
            throw new ArgumentException("strokeWidth должен быть больше 0.");
        return new MarkupStyle(ParseColor(s.Color), (float)s.StrokeWidth, null, MarkupLineStyle.Solid);
    }

    /// <summary>null из JSON → значения по умолчанию, обязательное shape и конечные числа проверяются.</summary>
    private static void Validate(ShapeSpec s)
    {
        if (string.IsNullOrWhiteSpace(s.Shape))
            throw new ArgumentException("Поле shape обязательно.");
        s.Color ??= "red";
        s.Points ??= "";
        s.StartEnding ??= "";
        s.EndEnding ??= "";
        foreach (var (name, v) in new[] { ("x", s.X), ("y", s.Y), ("width", s.Width), ("height", s.Height), ("strokeWidth", s.StrokeWidth) })
            if (!double.IsFinite(v))
                throw new ArgumentException($"Поле {name} должно быть конечным числом.");
    }

    // Клиент (LineEnding.GetMarkupLineEndings) отрисовывает только None и OpenArrow.
    private static MarkupLineEndings ParseEnding(string name, MarkupLineEndings fallback)
    {
        if (name == "") return fallback;
        return name.Trim().ToLowerInvariant() switch
        {
            "none" => MarkupLineEndings.None,
            "openarrow" => MarkupLineEndings.OpenArrow,
            _ => throw new ArgumentException($"Неподдерживаемое окончание линии: {name}. Используйте: none, openarrow")
        };
    }

    /// <summary>"x1,y1;x2,y2;..." (пиксели) → точки Markup.</summary>
    private static MarkupPoint[] ParsePoints(string points, int min, Func<double, double, MarkupPoint> convert)
    {
        var result = points.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(p =>
        {
            var xy = p.Split(',', StringSplitOptions.TrimEntries);
            if (xy.Length != 2
                || !double.TryParse(xy[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
                || !double.TryParse(xy[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
                || !double.IsFinite(x) || !double.IsFinite(y))
                throw new ArgumentException($"Некорректная точка '{p}': ожидается 'x,y'.");
            return convert(x, y);
        }).ToArray();
        if (result.Length < min)
            throw new ArgumentException($"Нужно минимум {min} точек в points (формат 'x1,y1;x2,y2;...'), получено {result.Length}.");
        return result;
    }
}
