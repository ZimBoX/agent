using System.Text.Json;
using System.Text.RegularExpressions;
using Anthropic.Models.Messages;
using ClosedXML.Excel;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace ClaudeAssistant.Api.Services.FileGeneration;

// Генерирует офисные файлы (Excel .xlsx / Word .docx) из структурированного
// запроса модели. Модель вызывает инструмент create_file (см. BuildCreateFileTool)
// с данными таблицы или markdown-текстом, а мы собираем настоящий файл через
// ClosedXML / DocumentFormat.OpenXml — не полагаясь на code execution агрегатора.
public partial class DocumentGenerationService
{
    private const int MaxFilenameLength = 80;

    public const string XlsxContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    public const string DocxContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    // Определение инструмента create_file для нативного пути (Anthropic Tool).
    // Один и тот же инструмент используется и в основном потоке, и в чате.
    public static Tool BuildCreateFileTool() =>
        new()
        {
            Name = "create_file",
            Description =
                "Создаёт файл и прикрепляет его к ответу для скачивания пользователем. "
                + "Используй, когда пользователь просит СДЕЛАТЬ/СФОРМИРОВАТЬ файл: таблицу (Excel) или документ (Word). "
                + "Для таблицы укажи format=\"xlsx\" и заполни sheets (первая строка каждого листа — заголовки столбцов). "
                + "Для документа укажи format=\"docx\" и передай содержимое в markdown (заголовки #/##/###, списки \"- \", таблицы через | ... |, **жирный**). "
                + "После создания файла кратко подтверди пользователю, что файл готов и приложен — не вставляй его содержимое в текст ответа целиком.",
            InputSchema = new()
            {
                Properties = new Dictionary<string, JsonElement>
                {
                    ["format"] = JsonSerializer.SerializeToElement(
                        new { type = "string", @enum = new[] { "xlsx", "docx" }, description = "Формат файла: xlsx (таблица) или docx (документ Word)" }
                    ),
                    ["filename"] = JsonSerializer.SerializeToElement(
                        new { type = "string", description = "Имя файла без расширения, например «Смета» или «Акт сверки»" }
                    ),
                    ["sheets"] = JsonSerializer.SerializeToElement(
                        new
                        {
                            type = "array",
                            description = "Только для xlsx: листы таблицы. Обычно один лист.",
                            items = new
                            {
                                type = "object",
                                properties = new
                                {
                                    name = new { type = "string", description = "Название листа (необязательно)" },
                                    rows = new
                                    {
                                        type = "array",
                                        description = "Строки таблицы; первая строка — заголовки столбцов. Каждая строка — массив значений ячеек (строками).",
                                        items = new { type = "array", items = new { type = "string" } },
                                    },
                                },
                                required = new[] { "rows" },
                            },
                        }
                    ),
                    ["markdown"] = JsonSerializer.SerializeToElement(
                        new { type = "string", description = "Только для docx: содержимое документа в формате Markdown" }
                    ),
                },
                Required = ["format", "filename"],
            },
        };

    // Собирает файл по входным данным инструмента. Бросает InvalidOperationException
    // с человекочитаемым сообщением при некорректном вводе — вызывающий tool-loop
    // вернёт его модели как результат инструмента, чтобы та поправилась.
    public (byte[] Bytes, string FileName, string ContentType) Generate(IReadOnlyDictionary<string, JsonElement> input)
    {
        var format = GetString(input, "format")?.Trim().ToLowerInvariant();
        var rawName = GetString(input, "filename");
        var baseName = SanitizeFilename(rawName);

        return format switch
        {
            "xlsx" => (GenerateXlsx(input, baseName), $"{baseName}.xlsx", XlsxContentType),
            "docx" => (GenerateDocx(input, baseName), $"{baseName}.docx", DocxContentType),
            _ => throw new InvalidOperationException("Неизвестный format: ожидается \"xlsx\" или \"docx\"."),
        };
    }

    // ===== Excel =====

    private static byte[] GenerateXlsx(IReadOnlyDictionary<string, JsonElement> input, string baseName)
    {
        if (!input.TryGetValue("sheets", out var sheetsEl) || sheetsEl.ValueKind != JsonValueKind.Array || sheetsEl.GetArrayLength() == 0)
        {
            throw new InvalidOperationException("Для xlsx нужно передать непустой массив sheets с строками (rows).");
        }

        using var workbook = new XLWorkbook();
        var sheetIndex = 0;
        foreach (var sheetEl in sheetsEl.EnumerateArray())
        {
            sheetIndex++;
            var sheetName = sheetEl.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String
                ? SanitizeSheetName(nameEl.GetString(), sheetIndex)
                : $"Лист{sheetIndex}";
            var ws = workbook.Worksheets.Add(sheetName);

            if (!sheetEl.TryGetProperty("rows", out var rowsEl) || rowsEl.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var r = 0;
            foreach (var rowEl in rowsEl.EnumerateArray())
            {
                if (rowEl.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }
                r++;
                var c = 0;
                foreach (var cellEl in rowEl.EnumerateArray())
                {
                    c++;
                    var cell = ws.Cell(r, c);
                    // Числа/даты набираем типизированно, чтобы Excel считал по ним и
                    // выравнивал вправо; иначе — как текст.
                    var raw = CellToString(cellEl);
                    if (double.TryParse(raw, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var num))
                    {
                        cell.Value = num;
                    }
                    else
                    {
                        cell.Value = raw;
                    }
                }
            }

            if (r > 0)
            {
                ws.Row(1).Style.Font.Bold = true;
                ws.Columns().AdjustToContents();
            }
        }

        using var ms = new MemoryStream();
        workbook.SaveAs(ms);
        return ms.ToArray();
    }

    private static string CellToString(JsonElement cell) =>
        cell.ValueKind switch
        {
            JsonValueKind.String => cell.GetString() ?? "",
            JsonValueKind.Number => cell.GetRawText(),
            JsonValueKind.True => "да",
            JsonValueKind.False => "нет",
            JsonValueKind.Null => "",
            _ => cell.GetRawText(),
        };

    // Excel запрещает в имени листа : \ / ? * [ ] и длину > 31.
    private static string SanitizeSheetName(string? name, int index)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return $"Лист{index}";
        }
        var cleaned = InvalidSheetChars().Replace(name, " ").Trim();
        if (cleaned.Length == 0)
        {
            return $"Лист{index}";
        }
        return cleaned.Length > 31 ? cleaned[..31] : cleaned;
    }

    // ===== Word =====

    private static byte[] GenerateDocx(IReadOnlyDictionary<string, JsonElement> input, string baseName)
    {
        var markdown = GetString(input, "markdown") ?? "";

        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var mainPart = doc.AddMainDocumentPart();
            mainPart.Document = new Document();
            var body = mainPart.Document.AppendChild(new Body());

            AppendMarkdown(body, markdown);

            if (!body.HasChildren)
            {
                body.AppendChild(new Paragraph(new Run(new Text(baseName))));
            }
            mainPart.Document.Save();
        }
        return ms.ToArray();
    }

    // Минимальный markdown → docx: заголовки (#/##/###), маркированные списки
    // (- / *), таблицы (| ... |) и абзацы; **жирный** внутри строки. Стили эмулируем
    // размером/жирностью шрифта, чтобы не тянуть styles-часть.
    private static void AppendMarkdown(Body body, string markdown)
    {
        var lines = markdown.ReplaceLineEndings("\n").Split('\n');
        var i = 0;
        while (i < lines.Length)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();

            // Таблица: несколько подряд идущих строк, начинающихся с '|'.
            if (trimmed.StartsWith('|'))
            {
                var tableLines = new List<string>();
                while (i < lines.Length && lines[i].TrimStart().StartsWith('|'))
                {
                    tableLines.Add(lines[i].Trim());
                    i++;
                }
                AppendTable(body, tableLines);
                continue;
            }

            i++;

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (trimmed.StartsWith("### "))
            {
                body.AppendChild(HeadingParagraph(trimmed[4..], 24));
            }
            else if (trimmed.StartsWith("## "))
            {
                body.AppendChild(HeadingParagraph(trimmed[3..], 28));
            }
            else if (trimmed.StartsWith("# "))
            {
                body.AppendChild(HeadingParagraph(trimmed[2..], 32));
            }
            else if (trimmed.StartsWith("- ") || trimmed.StartsWith("* "))
            {
                body.AppendChild(BulletParagraph(trimmed[2..]));
            }
            else
            {
                body.AppendChild(BodyParagraph(trimmed));
            }
        }
    }

    private static Paragraph HeadingParagraph(string text, int halfPointSize)
    {
        var p = new Paragraph();
        foreach (var run in InlineRuns(text, forceBold: true, halfPointSize))
        {
            p.AppendChild(run);
        }
        p.ParagraphProperties = new ParagraphProperties(new SpacingBetweenLines { Before = "160", After = "80" });
        return p;
    }

    private static Paragraph BulletParagraph(string text)
    {
        var p = new Paragraph();
        p.AppendChild(new Run(new Text("• ") { Space = SpaceProcessingModeValues.Preserve }));
        foreach (var run in InlineRuns(text, forceBold: false, null))
        {
            p.AppendChild(run);
        }
        p.ParagraphProperties = new ParagraphProperties(new Indentation { Left = "360" });
        return p;
    }

    private static Paragraph BodyParagraph(string text)
    {
        var p = new Paragraph();
        foreach (var run in InlineRuns(text, forceBold: false, null))
        {
            p.AppendChild(run);
        }
        return p;
    }

    // Разбивает строку на runs по **жирным** участкам.
    private static IEnumerable<Run> InlineRuns(string text, bool forceBold, int? halfPointSize)
    {
        foreach (Match m in BoldSegments().Matches(text))
        {
            var isBold = forceBold || m.Groups["b"].Success;
            var content = m.Groups["b"].Success ? m.Groups["b"].Value : m.Value;
            if (content.Length == 0)
            {
                continue;
            }

            var run = new Run();
            var props = new RunProperties();
            if (isBold)
            {
                props.AppendChild(new Bold());
            }
            if (halfPointSize is { } sz)
            {
                props.AppendChild(new FontSize { Val = sz.ToString() });
            }
            if (props.HasChildren)
            {
                run.AppendChild(props);
            }
            run.AppendChild(new Text(content) { Space = SpaceProcessingModeValues.Preserve });
            yield return run;
        }
    }

    private static void AppendTable(Body body, List<string> tableLines)
    {
        // Строки-разделители вида |---|---| пропускаем.
        var rows = tableLines
            .Where(l => !SeparatorRow().IsMatch(l))
            .Select(SplitTableRow)
            .Where(cells => cells.Count > 0)
            .ToList();
        if (rows.Count == 0)
        {
            return;
        }

        var table = new Table();
        table.AppendChild(
            new TableProperties(
                new TableBorders(
                    new TopBorder { Val = BorderValues.Single, Size = 4 },
                    new BottomBorder { Val = BorderValues.Single, Size = 4 },
                    new LeftBorder { Val = BorderValues.Single, Size = 4 },
                    new RightBorder { Val = BorderValues.Single, Size = 4 },
                    new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4 },
                    new InsideVerticalBorder { Val = BorderValues.Single, Size = 4 }
                ),
                new TableWidth { Width = "5000", Type = TableWidthUnitValues.Pct }
            )
        );

        var isHeader = true;
        foreach (var cells in rows)
        {
            var tr = new TableRow();
            foreach (var cellText in cells)
            {
                var tc = new TableCell();
                tc.AppendChild(new TableCellProperties(new TableCellWidth { Type = TableWidthUnitValues.Auto }));
                var p = new Paragraph();
                foreach (var run in InlineRuns(cellText, forceBold: isHeader, null))
                {
                    p.AppendChild(run);
                }
                if (!p.HasChildren)
                {
                    p.AppendChild(new Run(new Text("")));
                }
                tc.AppendChild(p);
                tr.AppendChild(tc);
            }
            table.AppendChild(tr);
            isHeader = false;
        }

        body.AppendChild(table);
        // Пустой абзац после таблицы — иначе Word может «слипнуть» её со следующим блоком.
        body.AppendChild(new Paragraph());
    }

    private static List<string> SplitTableRow(string line)
    {
        var trimmed = line.Trim().Trim('|');
        return trimmed.Split('|').Select(c => c.Trim()).ToList();
    }

    // ===== Общее =====

    private static string? GetString(IReadOnlyDictionary<string, JsonElement> input, string key) =>
        input.TryGetValue(key, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

    private static string SanitizeFilename(string? raw)
    {
        var name = (raw ?? "").Trim();
        foreach (var ch in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(ch, ' ');
        }
        name = name.Replace('.', ' ').Trim();
        if (name.Length == 0)
        {
            name = "Документ";
        }
        return name.Length > MaxFilenameLength ? name[..MaxFilenameLength].Trim() : name;
    }

    [GeneratedRegex(@"[:\\/?*\[\]]")]
    private static partial Regex InvalidSheetChars();

    // Захватывает либо **жирный** сегмент (группа b), либо обычный текст до
    // следующего **.
    [GeneratedRegex(@"\*\*(?<b>[^*]+)\*\*|[^*]+|\*")]
    private static partial Regex BoldSegments();

    [GeneratedRegex(@"^\|[\s:\-|]+\|?$")]
    private static partial Regex SeparatorRow();
}
