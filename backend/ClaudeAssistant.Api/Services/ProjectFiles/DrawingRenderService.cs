using System.Text;
using PDFtoImage;
using SkiaSharp;

namespace ClaudeAssistant.Api.Services.ProjectFiles;

// Регион страницы в долях 0..1, начало отсчёта — верхний-левый угол листа
// (как человек смотрит на чертёж): X/Y — левый-верхний угол вырезки, Width/Height — размеры.
public readonly record struct PageRegion(float X, float Y, float Width, float Height);

// Рендер PDF-страниц (в т.ч. метровых чертежей) в картинки для vision-модели —
// экономично: обзорный монтаж превью, чтобы НАЙТИ листы с таблицами объёмов, и
// вырезку конкретного региона в высоком разрешении, чтобы ПРОЧИТАТЬ цифры, не
// отдавая весь лист (после даунскейла крупного листа мелкий текст нечитаем).
//
// Работает по ПУТИ к файлу (PDFium читает лениво) — не грузит весь PDF в память,
// критично для метровых листов на скромном сервере. Результаты кэшируются на диск.
public class DrawingRenderService(RenderCacheService cache, ILogger<DrawingRenderService> logger)
{
    // Потолок длинной стороны выходной картинки. Совпадает с внутренним даунскейлом
    // Anthropic (~1568px): больше отдавать бессмысленно (те же токены/качество), а для
    // памяти сервера критично не рендерить метровый лист в гигантский bitmap.
    public const int MaxLongSidePx = 1560;

    private const int ThumbCellWidth = 360;
    private const int ThumbCellHeight = 250;
    private const int MontageColumns = 4;
    private const int MontageMaxPages = 24; // 4×6 листов на монтаж
    private const int Padding = 8;
    private const int LabelHeight = 22;
    private const int MaxSnippetLength = 240;

    public int GetPageCount(string pdfPath)
    {
        using var fs = File.OpenRead(pdfPath);
        return Conversion.GetPageCount(fs, leaveOpen: true, (string?)null);
    }

    private static long Mtime(string path) => File.GetLastWriteTimeUtc(path).Ticks;

    // PDFtoImage: строковый оверлоуд трактует ввод как base64, а не путь — поэтому
    // читаем через Stream (PDFium читает лениво, весь файл в память не грузится).
    private static IList<System.Drawing.SizeF> PageSizes(string path)
    {
        using var fs = File.OpenRead(path);
        return Conversion.GetPageSizes(fs, leaveOpen: true, (string?)null);
    }

    private static SKBitmap RenderBitmap(string path, int pageIndex, RenderOptions options)
    {
        using var fs = File.OpenRead(path);
        return Conversion.ToImage(fs, pageIndex, leaveOpen: true, (string?)null, options);
    }

    // Рендерит одну страницу целиком (region=null) или её регион, с ограничением
    // разрешения/памяти. page1 — номер листа с 1 (как у пользователя).
    public byte[] RenderPage(string pdfPath, int page1, PageRegion? region, int maxLongSidePx = MaxLongSidePx)
    {
        var regionKey = region is { } r ? $"{r.X:0.###},{r.Y:0.###},{r.Width:0.###},{r.Height:0.###}" : "full";
        var key = $"page|{pdfPath}|{Mtime(pdfPath)}|{page1}|{regionKey}|{maxLongSidePx}";
        return cache.GetOrCreate(key, () => RenderPageCore(pdfPath, page1 - 1, region, maxLongSidePx));
    }

    private static byte[] RenderPageCore(string pdfPath, int pageIndex, PageRegion? region, int maxLongSidePx)
    {
        var sizes = PageSizes(pdfPath);
        if (pageIndex < 0 || pageIndex >= sizes.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex), $"Лист {pageIndex + 1} вне диапазона (всего {sizes.Count}).");
        }
        var size = sizes[pageIndex]; // точки PDF (1/72")

        System.Drawing.RectangleF? bounds = null;
        float regionWidthPt = size.Width,
            regionHeightPt = size.Height;
        if (region is { } r)
        {
            // Начало координат Bounds — верхний-левый угол (проверено рендером).
            var x = Math.Clamp(r.X, 0f, 0.99f);
            var y = Math.Clamp(r.Y, 0f, 0.99f);
            var w = Math.Clamp(r.Width, 0.02f, 1f - x);
            var h = Math.Clamp(r.Height, 0.02f, 1f - y);
            bounds = new System.Drawing.RectangleF(x * size.Width, y * size.Height, w * size.Width, h * size.Height);
            regionWidthPt = w * size.Width;
            regionHeightPt = h * size.Height;
        }

        var scale = maxLongSidePx / Math.Max(regionWidthPt, regionHeightPt);
        var widthPx = Math.Max(1, (int)Math.Round(regionWidthPt * scale));
        var heightPx = Math.Max(1, (int)Math.Round(regionHeightPt * scale));

        var options = new RenderOptions
        {
            Width = widthPx,
            Height = heightPx,
            WithAspectRatio = true,
            Bounds = bounds,
            UseTiling = true,
            BackgroundColor = SKColors.White,
            WithAnnotations = true,
        };
        using var bitmap = RenderBitmap(pdfPath, pageIndex, options);
        return EncodeJpeg(bitmap, 82);
    }

    // Монтаж превью всех страниц (несколько картинок, если листов много) с подписью
    // номера над каждым — модель видит все листы разом и выбирает, где таблицы объёмов.
    public List<byte[]> RenderMontages(string pdfPath)
    {
        var pageCount = GetPageCount(pdfPath);
        var mtime = Mtime(pdfPath);
        var montages = new List<byte[]>();
        for (var start = 0; start < pageCount; start += MontageMaxPages)
        {
            var startPage = start;
            var count = Math.Min(MontageMaxPages, pageCount - start);
            var key = $"montage|{pdfPath}|{mtime}|{startPage}|{count}";
            montages.Add(cache.GetOrCreate(key, () => RenderMontageBatch(pdfPath, startPage, count)));
        }
        return montages;
    }

    private byte[] RenderMontageBatch(string pdfPath, int startPage, int count)
    {
        var rows = (count + MontageColumns - 1) / MontageColumns;
        var cellTotalH = ThumbCellHeight + LabelHeight;
        var totalW = MontageColumns * ThumbCellWidth + (MontageColumns + 1) * Padding;
        var totalH = rows * cellTotalH + (rows + 1) * Padding;

        using var surface = SKSurface.Create(new SKImageInfo(totalW, totalH));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.White);

        using var titleFont = new SKFont(SKTypeface.Default, 15) { Embolden = true };
        using var textPaint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
        using var borderPaint = new SKPaint { Color = SKColors.LightGray, IsStroke = true, StrokeWidth = 1 };

        for (var i = 0; i < count; i++)
        {
            var pageIndex = startPage + i;
            var col = i % MontageColumns;
            var row = i / MontageColumns;
            var cellX = Padding + col * (ThumbCellWidth + Padding);
            var cellY = Padding + row * (cellTotalH + Padding);

            canvas.DrawText($"Лист {pageIndex + 1}", cellX + 2, cellY + 16, SKTextAlign.Left, titleFont, textPaint);

            try
            {
                using var thumb = RenderBitmap(
                    pdfPath,
                    pageIndex,
                    new RenderOptions
                    {
                        Width = ThumbCellWidth,
                        Height = ThumbCellHeight,
                        WithAspectRatio = true,
                        UseTiling = true,
                        BackgroundColor = SKColors.White,
                    }
                );
                var thumbX = cellX + (ThumbCellWidth - thumb.Width) / 2;
                var thumbY = cellY + LabelHeight + (ThumbCellHeight - thumb.Height) / 2;
                canvas.DrawBitmap(thumb, thumbX, thumbY);
                canvas.DrawRect(thumbX, thumbY, thumb.Width, thumb.Height, borderPaint);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Не удалось отрендерить превью листа {Page}", pageIndex + 1);
            }
        }

        using var image = surface.Snapshot();
        using var montageBitmap = SKBitmap.FromImage(image);
        return EncodeJpeg(montageBitmap, 72);
    }

    // Поиск по текстовому слою PDF: возвращает (номер листа, фрагмент) для листов,
    // где встретился запрос. У сканов текстового слоя нет — вернётся пусто.
    public IReadOnlyList<(int Page, string Snippet)> SearchText(string pdfPath, string query)
    {
        query = query.Trim();
        if (query.Length == 0)
        {
            return [];
        }
        var pages = GetPageTexts(pdfPath);
        var hits = new List<(int, string)>();
        for (var i = 0; i < pages.Length; i++)
        {
            var idx = pages[i].IndexOf(query, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                hits.Add((i + 1, Snippet(pages[i], idx, query.Length)));
            }
        }
        return hits;
    }

    // Есть ли у PDF вообще текстовый слой (иначе это скан — только через рендер).
    public bool HasTextLayer(string pdfPath) => GetPageTexts(pdfPath).Any(t => t.Trim().Length > 0);

    private string[] GetPageTexts(string pdfPath)
    {
        var key = $"pagetext|{pdfPath}|{Mtime(pdfPath)}";
        var bytes = cache.GetOrCreate(key, () => Encoding.UTF8.GetBytes(string.Join('\f', ExtractPageTexts(pdfPath))));
        return Encoding.UTF8.GetString(bytes).Split('\f');
    }

    private static string[] ExtractPageTexts(string pdfPath)
    {
        using var document = UglyToad.PdfPig.PdfDocument.Open(pdfPath);
        return document.GetPages().Select(p => (p.Text ?? "").ReplaceLineEndings(" ")).ToArray();
    }

    private static string Snippet(string text, int matchIndex, int matchLength)
    {
        var start = Math.Max(0, matchIndex - MaxSnippetLength / 2);
        var end = Math.Min(text.Length, matchIndex + matchLength + MaxSnippetLength / 2);
        var snippet = text.Substring(start, end - start).Trim();
        return (start > 0 ? "…" : "") + snippet + (end < text.Length ? "…" : "");
    }

    // Кодирует в JPEG. JPEG-энкодер SkiaSharp не поддерживает часть цветовых типов
    // (Gray8, некоторые alpha) — молча падает в PNG-фолбэк, из-за чего Anthropic ругался
    // «jpeg media type, but image appears to be png». Поэтому предварительно
    // перерисовываем в стандартный непрозрачный RGBA. Media type всё равно определяем
    // по реальным байтам (DetectMediaType) — на случай, если JPEG-энкодера в сборке нет.
    private static byte[] EncodeJpeg(SKBitmap bitmap, int quality)
    {
        var info = new SKImageInfo(bitmap.Width, bitmap.Height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using var normalized = new SKBitmap(info);
        using (var canvas = new SKCanvas(normalized))
        {
            canvas.Clear(SKColors.White);
            canvas.DrawBitmap(bitmap, 0, 0);
        }
        using var image = SKImage.FromBitmap(normalized);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, quality);
        if (data is not null)
        {
            return data.ToArray();
        }
        // JPEG-энкодер недоступен — отдаём PNG (media type определится по байтам).
        using var png = image.Encode();
        return png.ToArray();
    }

    // Тип картинки по «магическим» байтам — чтобы пометить ImageBlockParam верно,
    // независимо от того, что реально закодировал SkiaSharp.
    public static string DetectMediaType(byte[] image) =>
        image.Length >= 3 && image[0] == 0xFF && image[1] == 0xD8 && image[2] == 0xFF ? "image/jpeg" : "image/png";
}
