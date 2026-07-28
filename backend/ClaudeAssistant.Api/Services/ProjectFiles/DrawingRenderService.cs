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

    private const int ThumbCellWidth = 260;
    private const int ThumbCellHeight = 180;
    private const int MontageColumns = 4;
    private const int MontageMaxPages = 24; // 4×6 листов на монтаж
    private const int Padding = 8;
    private const int LabelHeight = 20;
    private const int MaxSnippetLength = 240;

    // Потолок суммарного размера картинок обзора за один вызов. Тонкие линии чертежей
    // плохо жмутся в JPEG, а вход агрегатора ограничен 8 МБ — держим обзор лёгким;
    // если листов больше, чем влезло, модель листает через параметр from.
    private const long MontageByteBudget = 2_200_000;

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

    public record MontageResult(List<byte[]> Montages, int FromPage, int LastPageShown, int TotalPages);

    // Монтаж превью листов с подписью номера — модель видит листы разом и выбирает,
    // где таблицы объёмов. Начинает с fromPage (с 1); отдаёт монтажи, пока не упрётся
    // в MontageByteBudget (вход агрегатора ограничен 8 МБ), — остальное листается
    // повторным вызовом с бо́льшим from.
    public MontageResult RenderMontages(string pdfPath, int fromPage = 1)
    {
        var pageCount = GetPageCount(pdfPath);
        var mtime = Mtime(pdfPath);
        var start = Math.Clamp(fromPage - 1, 0, Math.Max(0, pageCount - 1));

        var montages = new List<byte[]>();
        long totalBytes = 0;
        var lastShown = start;
        while (start < pageCount)
        {
            var startPage = start;
            var count = Math.Min(MontageMaxPages, pageCount - start);
            var key = $"montage|{pdfPath}|{mtime}|{startPage}|{count}|v2";
            var montage = cache.GetOrCreate(key, () => RenderMontageBatch(pdfPath, startPage, count));
            // Первый монтаж отдаём всегда (даже если крупнее бюджета), дальше — по бюджету.
            if (montages.Count > 0 && totalBytes + montage.Length > MontageByteBudget)
            {
                break;
            }
            montages.Add(montage);
            totalBytes += montage.Length;
            lastShown = startPage + count;
            start += MontageMaxPages;
        }
        return new MontageResult(montages, start > 0 ? Math.Clamp(fromPage, 1, pageCount) : 1, lastShown, pageCount);
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
        return EncodeJpeg(montageBitmap, 58);
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

    private static byte[] EncodeJpeg(SKBitmap bitmap, int quality) => SkiaImages.EncodeJpeg(bitmap, quality);
}
