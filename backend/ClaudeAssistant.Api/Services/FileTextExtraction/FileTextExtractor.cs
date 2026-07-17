using System.Text;
using ClosedXML.Excel;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using UglyToad.PdfPig;

namespace ClaudeAssistant.Api.Services.FileTextExtraction;

// Ровно одно из двух заполнено: Text — обычный случай (docx/xlsx, либо pdf с
// текстовым слоем); Images — pdf без текстового слоя (скан), рендер постранично
// в PNG вместо текста (см. FileTextExtractor.ExtractPdfAsync).
public record ExtractionResult(string? Text, IReadOnlyList<byte[]> Images);

public class FileTextExtractor
{
    // Ограничивает число страниц скана, которые уходят в модель как картинки —
    // без этого многостраничный скан раздул бы и запрос, и стоимость без предела.
    private const int MaxRenderedPdfPages = 15;

    public async Task<ExtractionResult> ExtractAsync(Stream stream, string fileName)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        return extension switch
        {
            ".docx" => new ExtractionResult(ExtractDocx(stream), []),
            ".xlsx" => new ExtractionResult(ExtractXlsx(stream), []),
            ".pdf" => await ExtractPdfAsync(stream, fileName),
            _ => throw new UnsupportedFileFormatException(fileName),
        };
    }

    private static string ExtractDocx(Stream stream)
    {
        using var doc = WordprocessingDocument.Open(stream, false);
        var body = doc.MainDocumentPart?.Document?.Body;
        if (body is null)
        {
            return string.Empty;
        }

        var paragraphs = body.Descendants<Paragraph>().Select(p => p.InnerText);
        return string.Join("\n", paragraphs);
    }

    private static string ExtractXlsx(Stream stream)
    {
        using var workbook = new XLWorkbook(stream);
        var sb = new StringBuilder();

        foreach (var worksheet in workbook.Worksheets)
        {
            sb.AppendLine($"# {worksheet.Name}");
            var rangeUsed = worksheet.RangeUsed();
            if (rangeUsed is null)
            {
                continue;
            }

            // row.Cells(first, last) ждёт номера колонок ОТНОСИТЕЛЬНО самой строки
            // (1 = первая колонка диапазона), а не абсолютные номера колонок листа —
            // раньше здесь передавались абсолютные (rangeUsed.FirstColumn()/
            // LastColumn()), из-за чего диапазон уезжал за пределы реально
            // используемых ячеек и падал с ArgumentOutOfRangeException на любом
            // xlsx, где данные начинались не с колонки A.
            var columnCount = rangeUsed.ColumnCount();
            foreach (var row in rangeUsed.RowsUsed())
            {
                var cells = row.Cells(1, columnCount).Select(c => c.GetString());
                sb.AppendLine(string.Join("\t", cells));
            }
        }

        return sb.ToString();
    }

    // Сначала пробует обычное извлечение текстового слоя (как раньше). Если
    // слоя нет (типичный случай — скан, сфотографированный документ) — вместо
    // ошибки "нечитаемый PDF" рендерит страницы в PNG и отдаёт их как картинки:
    // модель одинаково хорошо читает текст что с текстового слоя, что с
    // изображения (проверено smoke-тестом vision через агрегатор перед тем, как
    // строить эту ветку — см. IMPLEMENTATION_CHECKLIST.md).
    private static async Task<ExtractionResult> ExtractPdfAsync(Stream stream, string fileName)
    {
        using var bufferStream = new MemoryStream();
        await stream.CopyToAsync(bufferStream);
        var pdfBytes = bufferStream.ToArray();

        string text;
        try
        {
            using var textStream = new MemoryStream(pdfBytes);
            using var document = PdfDocument.Open(textStream);
            var sb = new StringBuilder();

            foreach (var page in document.GetPages())
            {
                sb.AppendLine(page.Text);
            }

            text = sb.ToString();
        }
        catch (Exception ex) when (ex is not UnreadablePdfException)
        {
            // Битый файл, зашифрованный PDF и т.п. — для пользователя это всё одна
            // и та же ошибка "нечитаемый PDF" (в отличие от "нет текстового слоя",
            // который обрабатывается ниже как отдельный, не ошибочный случай).
            throw new UnreadablePdfException(fileName);
        }

        if (!string.IsNullOrWhiteSpace(text))
        {
            return new ExtractionResult(text, []);
        }

        int pageCount;
        try
        {
            pageCount = PDFtoImage.Conversion.GetPageCount(pdfBytes);
        }
        catch (Exception)
        {
            throw new UnreadablePdfException(fileName);
        }

        if (pageCount == 0)
        {
            throw new UnreadablePdfException(fileName);
        }

        var pagesToRender = Math.Min(pageCount, MaxRenderedPdfPages);
        var images = new List<byte[]>();
        for (var i = 0; i < pagesToRender; i++)
        {
            using var pngStream = new MemoryStream();
            PDFtoImage.Conversion.SavePng(pngStream, pdfBytes, i, password: null, options: new PDFtoImage.RenderOptions(Dpi: 150));
            images.Add(pngStream.ToArray());
        }

        return new ExtractionResult(null, images);
    }
}
