namespace PilotMCP;

/// <summary>
/// Размеры страниц документа в DIP (1/96") — в тех же единицах, в которых GetDocumentPageImage
/// рендерит страницу (XPS: FixedPage Width/Height, PDF: размер страницы PdfPig * 96/72).
/// </summary>
public static class PageGeometry
{
    /// <summary>Размеры запрошенных страниц (индекс с 0). Страницы вне документа в результат не попадают.</summary>
    public static Dictionary<int, (double Width, double Height)> Load(string path, string ext, IEnumerable<int> pages)
    {
        var result = new Dictionary<int, (double Width, double Height)>();
        if (ext == ".pdf")
        {
            using var pdf = UglyToad.PdfPig.PdfDocument.Open(path);
            foreach (var page in pages.Distinct().Where(p => p >= 0 && p < pdf.NumberOfPages))
            {
                var pg = pdf.GetPage(page + 1);
                result[page] = (pg.Width * 96.0 / 72.0, pg.Height * 96.0 / 72.0);
            }
        }
        else
        {
            var sizes = XpsTextExtractor.GetPageSizes(path);
            foreach (var page in pages.Distinct().Where(p => p >= 0 && p < sizes.Count))
                result[page] = sizes[page];
        }
        return result;
    }
}
