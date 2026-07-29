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
                + "Для документа укажи format=\"docx\" и передай содержимое в markdown: заголовки #/##/### (станут стилями Word), "
                + "маркированные списки \"- \" и нумерованные \"1. \" (в т.ч. вложенные — отступ 2 пробела на уровень), таблицы через | ... |, "
                + "**жирный**, *курсив*. Структурируй документ как деловой: заголовок, разделы, списки, таблицы. "
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
        // qwen и другие OpenAI-совместимые модели через агрегатор часто присылают
        // вложенные аргументы СТРОКАМИ (JSON внутри строки), а не массивами — из-за
        // этого раньше create_file падал на «нужен массив sheets» и модель уходила в
        // цикл повторов. Принимаем и массив, и строку-JSON на всех уровнях (sheets,
        // rows, каждая строка). keep держит распарсенные документы живыми.
        var keep = new List<JsonDocument>();
        try
        {
            var sheetsEl = CoerceJsonArray(input.TryGetValue("sheets", out var se) ? se : default, keep);
            if (sheetsEl.ValueKind != JsonValueKind.Array || sheetsEl.GetArrayLength() == 0)
            {
                throw new InvalidOperationException("Для xlsx нужен непустой массив sheets, каждый лист — {name?, rows:[[...]]}.");
            }

            using var workbook = new XLWorkbook();
            var sheetIndex = 0;
            foreach (var sheetEl in sheetsEl.EnumerateArray())
            {
                sheetIndex++;
                var sheetName = sheetEl.ValueKind == JsonValueKind.Object
                    && sheetEl.TryGetProperty("name", out var nameEl)
                    && nameEl.ValueKind == JsonValueKind.String
                    ? SanitizeSheetName(nameEl.GetString(), sheetIndex)
                    : $"Лист{sheetIndex}";
                var ws = workbook.Worksheets.Add(sheetName);

                // rows — из свойства объекта; допускаем и случай, когда сам лист уже
                // является массивом строк (модель прислала sheets как [[...],[...]]).
                var rowsEl = sheetEl.ValueKind == JsonValueKind.Object && sheetEl.TryGetProperty("rows", out var rp)
                    ? CoerceJsonArray(rp, keep)
                    : sheetEl.ValueKind == JsonValueKind.Array
                        ? sheetEl
                        : CoerceJsonArray(sheetEl, keep);
                if (rowsEl.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var r = 0;
                foreach (var rowRaw in rowsEl.EnumerateArray())
                {
                    var rowEl = CoerceJsonArray(rowRaw, keep);
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
        finally
        {
            foreach (var d in keep)
            {
                d.Dispose();
            }
        }
    }

    // Возвращает JsonElement-массив: если уже массив — как есть; если строка с JSON
    // внутри (частый случай у qwen/OpenAI-совместимых) — парсит её; иначе default
    // (ValueKind=Undefined). Распарсенный документ кладём в keep, чтобы элемент не
    // «протух» (JsonElement ссылается на живой JsonDocument).
    private static JsonElement CoerceJsonArray(JsonElement el, List<JsonDocument> keep)
    {
        if (el.ValueKind == JsonValueKind.Array)
        {
            return el;
        }
        if (el.ValueKind == JsonValueKind.String)
        {
            var s = el.GetString();
            if (!string.IsNullOrWhiteSpace(s))
            {
                try
                {
                    var doc = JsonDocument.Parse(s);
                    keep.Add(doc);
                    return doc.RootElement;
                }
                catch (JsonException)
                {
                    // не JSON — вернём default ниже
                }
            }
        }
        return default;
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
    // Типографика делового документа: шрифт по умолчанию Times New Roman 12,
    // заголовки — НАСТОЯЩИМИ стилями Word (Heading1-3, видны в области навигации),
    // списки — настоящей нумерацией (маркированные/нумерованные, до 3 уровней),
    // абзацы выключены по ширине, аккуратные таблицы с шапкой. Важно: read_document
    // извлекает из исходных файлов только ТЕКСТ (без разметки), поэтому «скопировать»
    // оформление конкретного оригинала невозможно — генерируем опрятный документ
    // по структуре markdown, который передала модель.

    private const string BodyFont = "Times New Roman";
    private const string BodySizeHalfPoints = "24"; // 12pt (sz — половинки пункта)

    private static byte[] GenerateDocx(IReadOnlyDictionary<string, JsonElement> input, string baseName)
    {
        var markdown = GetString(input, "markdown") ?? "";

        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var mainPart = doc.AddMainDocumentPart();
            mainPart.Document = new Document();
            AddStyles(mainPart);
            AddNumbering(mainPart);
            var body = mainPart.Document.AppendChild(new Body());

            AppendMarkdown(body, markdown);

            if (!body.HasChildren)
            {
                body.AppendChild(BodyParagraph(baseName));
            }

            // Поля и размер страницы (A4) — sectPr должен быть последним в body.
            body.AppendChild(
                new SectionProperties(
                    new PageSize { Width = 11906, Height = 16838 },
                    new PageMargin { Top = 1134, Bottom = 1134, Left = 1418, Right = 850, Header = 708, Footer = 708, Gutter = 0 }
                )
            );
            mainPart.Document.Save();
        }
        return ms.ToArray();
    }

    // Стили документа: docDefaults (шрифт/интервал) + Normal + Heading1-3.
    private static void AddStyles(MainDocumentPart mainPart)
    {
        var part = mainPart.AddNewPart<StyleDefinitionsPart>();
        var styles = new Styles(
            new DocDefaults(
                new RunPropertiesDefault(
                    new RunPropertiesBaseStyle(
                        new RunFonts { Ascii = BodyFont, HighAnsi = BodyFont, ComplexScript = BodyFont },
                        new FontSize { Val = BodySizeHalfPoints },
                        new FontSizeComplexScript { Val = BodySizeHalfPoints }
                    )
                ),
                new ParagraphPropertiesDefault(
                    new ParagraphPropertiesBaseStyle(
                        new SpacingBetweenLines { After = "160", Line = "276", LineRule = LineSpacingRuleValues.Auto }
                    )
                )
            ),
            new Style(new StyleName { Val = "Normal" }) { Type = StyleValues.Paragraph, StyleId = "Normal", Default = true },
            HeadingStyle("Heading1", "heading 1", "34", "1F3864", 0),
            HeadingStyle("Heading2", "heading 2", "28", "2F5496", 1),
            HeadingStyle("Heading3", "heading 3", "26", "2F5496", 2)
        );
        part.Styles = styles;
    }

    private static Style HeadingStyle(string id, string name, string sizeHalfPoints, string colorHex, int outlineLevel) =>
        new(
            new StyleName { Val = name },
            new BasedOn { Val = "Normal" },
            new NextParagraphStyle { Val = "Normal" },
            new StyleParagraphProperties(
                new KeepNext(),
                new SpacingBetweenLines { Before = "240", After = "80", Line = "276", LineRule = LineSpacingRuleValues.Auto },
                new OutlineLevel { Val = outlineLevel }
            ),
            new StyleRunProperties(
                new Bold(),
                new Color { Val = colorHex },
                new FontSize { Val = sizeHalfPoints },
                new FontSizeComplexScript { Val = sizeHalfPoints }
            )
        )
        {
            Type = StyleValues.Paragraph,
            StyleId = id,
        };

    // Определения нумерации: numId=1 — маркированный список, numId=2 — нумерованный;
    // по 3 уровня вложенности у каждого.
    private static void AddNumbering(MainDocumentPart mainPart)
    {
        var part = mainPart.AddNewPart<NumberingDefinitionsPart>();
        part.Numbering = new Numbering(
            BuildAbstractNum(0, bullet: true),
            BuildAbstractNum(1, bullet: false),
            new NumberingInstance(new AbstractNumId { Val = 0 }) { NumberID = 1 },
            new NumberingInstance(new AbstractNumId { Val = 1 }) { NumberID = 2 }
        );
    }

    private static AbstractNum BuildAbstractNum(int id, bool bullet)
    {
        var abstractNum = new AbstractNum(new MultiLevelType { Val = MultiLevelValues.HybridMultilevel }) { AbstractNumberId = id };
        for (var lvl = 0; lvl < 3; lvl++)
        {
            var left = 720 * (lvl + 1);
            var level = new Level(
                new StartNumberingValue { Val = 1 },
                new NumberingFormat { Val = bullet ? NumberFormatValues.Bullet : NumberFormatValues.Decimal },
                new LevelText { Val = bullet ? "" : $"%{lvl + 1}." },
                new LevelJustification { Val = LevelJustificationValues.Left },
                new PreviousParagraphProperties(new Indentation { Left = left.ToString(), Hanging = "360" })
            )
            {
                LevelIndex = lvl,
            };
            if (bullet)
            {
                level.Append(new NumberingSymbolRunProperties(new RunFonts { Ascii = "Symbol", HighAnsi = "Symbol" }));
            }
            abstractNum.Append(level);
        }
        return abstractNum;
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

            // Уровень вложенности списка — по ведущим пробелам (2 пробела = уровень).
            var indentLevel = Math.Min(2, (line.Length - trimmed.Length) / 2);

            if (trimmed.StartsWith("#### ") || trimmed.StartsWith("### "))
            {
                body.AppendChild(HeadingParagraph(StripPrefix(trimmed), "Heading3"));
            }
            else if (trimmed.StartsWith("## "))
            {
                body.AppendChild(HeadingParagraph(trimmed[3..], "Heading2"));
            }
            else if (trimmed.StartsWith("# "))
            {
                body.AppendChild(HeadingParagraph(trimmed[2..], "Heading1"));
            }
            else if (trimmed.StartsWith("- ") || trimmed.StartsWith("* "))
            {
                body.AppendChild(ListParagraph(trimmed[2..], numberingId: 1, indentLevel));
            }
            else if (NumberedItem().Match(trimmed) is { Success: true } numbered)
            {
                body.AppendChild(ListParagraph(numbered.Groups["t"].Value, numberingId: 2, indentLevel));
            }
            else
            {
                body.AppendChild(BodyParagraph(trimmed));
            }
        }
    }

    private static string StripPrefix(string headingLine)
    {
        var hashEnd = headingLine.IndexOf(' ');
        return hashEnd < 0 ? headingLine : headingLine[(hashEnd + 1)..];
    }

    private static Paragraph HeadingParagraph(string text, string styleId)
    {
        var p = new Paragraph(new ParagraphProperties(new ParagraphStyleId { Val = styleId }));
        foreach (var run in InlineRuns(text))
        {
            p.AppendChild(run);
        }
        return p;
    }

    private static Paragraph ListParagraph(string text, int numberingId, int level)
    {
        var p = new Paragraph(
            new ParagraphProperties(
                new NumberingProperties(new NumberingLevelReference { Val = level }, new NumberingId { Val = numberingId }),
                new Justification { Val = JustificationValues.Both }
            )
        );
        foreach (var run in InlineRuns(text))
        {
            p.AppendChild(run);
        }
        return p;
    }

    private static Paragraph BodyParagraph(string text)
    {
        var p = new Paragraph(new ParagraphProperties(new Justification { Val = JustificationValues.Both }));
        foreach (var run in InlineRuns(text))
        {
            p.AppendChild(run);
        }
        return p;
    }

    // Разбивает строку на runs по инлайновой разметке: ***жирный курсив***,
    // **жирный**, *курсив*. forceBold — сделать весь текст жирным (шапка таблицы).
    private static IEnumerable<Run> InlineRuns(string text, bool forceBold = false)
    {
        foreach (Match m in InlineSegments().Matches(text))
        {
            bool bold = forceBold,
                italic = false;
            string content;
            if (m.Groups["bi"].Success)
            {
                content = m.Groups["bi"].Value;
                bold = true;
                italic = true;
            }
            else if (m.Groups["b"].Success)
            {
                content = m.Groups["b"].Value;
                bold = true;
            }
            else if (m.Groups["i"].Success)
            {
                content = m.Groups["i"].Value;
                italic = true;
            }
            else
            {
                content = m.Value;
            }

            if (content.Length == 0)
            {
                continue;
            }

            var run = new Run();
            if (bold || italic)
            {
                var props = new RunProperties();
                if (bold)
                {
                    props.AppendChild(new Bold());
                }
                if (italic)
                {
                    props.AppendChild(new Italic());
                }
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
        // Порядок дочерних элементов в OpenXML строгий: границы top→left→bottom→right→insideH→insideV.
        table.AppendChild(
            new TableProperties(
                new TableWidth { Width = "5000", Type = TableWidthUnitValues.Pct },
                new TableBorders(
                    new TopBorder { Val = BorderValues.Single, Size = 4, Color = "auto" },
                    new LeftBorder { Val = BorderValues.Single, Size = 4, Color = "auto" },
                    new BottomBorder { Val = BorderValues.Single, Size = 4, Color = "auto" },
                    new RightBorder { Val = BorderValues.Single, Size = 4, Color = "auto" },
                    new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4, Color = "auto" },
                    new InsideVerticalBorder { Val = BorderValues.Single, Size = 4, Color = "auto" }
                ),
                new TableLook { Val = "04A0" }
            )
        );

        // tblGrid обязателен и должен идти до строк: по колонке на каждый столбец.
        var columnCount = rows.Max(r => r.Count);
        var grid = new TableGrid();
        for (var c = 0; c < columnCount; c++)
        {
            grid.AppendChild(new GridColumn());
        }
        table.AppendChild(grid);

        var isHeader = true;
        foreach (var cells in rows)
        {
            var tr = new TableRow();
            foreach (var cellText in cells)
            {
                // Порядок в tcPr строгий: tcW → shd → vAlign.
                var cellProps = new TableCellProperties(new TableCellWidth { Type = TableWidthUnitValues.Auto });
                // Шапка — жирная на светло-голубой заливке.
                if (isHeader)
                {
                    cellProps.AppendChild(new Shading { Val = ShadingPatternValues.Clear, Color = "auto", Fill = "D9E2F3" });
                }
                cellProps.AppendChild(new TableCellVerticalAlignment { Val = TableVerticalAlignmentValues.Center });

                var p = new Paragraph(new ParagraphProperties(new SpacingBetweenLines { After = "0", Line = "240", LineRule = LineSpacingRuleValues.Auto }));
                foreach (var run in InlineRuns(cellText, forceBold: isHeader))
                {
                    p.AppendChild(run);
                }
                if (!p.Elements<Run>().Any())
                {
                    p.AppendChild(new Run(new Text("")));
                }

                var tc = new TableCell();
                tc.AppendChild(cellProps);
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

    // Инлайновая разметка: ***жирный курсив*** (bi), **жирный** (b), *курсив* (i),
    // обычный текст, либо одиночная «*».
    [GeneratedRegex(@"\*\*\*(?<bi>[^*]+)\*\*\*|\*\*(?<b>[^*]+)\*\*|\*(?<i>[^*]+)\*|[^*]+|\*")]
    private static partial Regex InlineSegments();

    // Пункт нумерованного списка: «1. текст» или «1) текст».
    [GeneratedRegex(@"^\d+[.)]\s+(?<t>.*)$")]
    private static partial Regex NumberedItem();

    [GeneratedRegex(@"^\|[\s:\-|]+\|?$")]
    private static partial Regex SeparatorRow();
}
