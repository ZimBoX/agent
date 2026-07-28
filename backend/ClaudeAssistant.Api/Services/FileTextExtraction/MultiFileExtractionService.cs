using System.Text;
using Microsoft.AspNetCore.Http;

namespace ClaudeAssistant.Api.Services.FileTextExtraction;

public record CombinedExtraction(string CombinedText, IReadOnlyList<byte[]> Images);

public class MultiFileExtractionService(FileTextExtractor extractor)
{
    public async Task<CombinedExtraction> ExtractAndCombineAsync(IReadOnlyList<IFormFile> files)
    {
        ValidateLimits(files);

        var sb = new StringBuilder();
        var images = new List<byte[]>();

        foreach (var file in files)
        {
            await using var stream = file.OpenReadStream();
            var result = await extractor.ExtractAsync(stream, file.FileName);

            if (result.Text is not null)
            {
                sb.AppendLine($"===== {file.FileName} =====");
                sb.AppendLine(result.Text);
                sb.AppendLine();
            }
            else
            {
                // PDF без текстового слоя (скан) — сам текст в комбинированную
                // строку не попадает, только пометка; страницы уходят отдельно как
                // изображения (см. FileTextExtractor.ExtractPdfAsync), их подключает
                // вызывающий код при обращении к модели.
                sb.AppendLine($"===== {file.FileName} (без текстового слоя — передано как изображение) =====");
                sb.AppendLine();
                images.AddRange(result.Images);
            }
        }

        return new CombinedExtraction(sb.ToString(), images);
    }

    // Публичный — используется и tool-режимом больших вложений, который валидирует
    // пачку теми же правилами, но содержимое не извлекает (см. RequestsController).
    public static void ValidateLimits(IReadOnlyList<IFormFile> files)
    {
        if (files.Count == 0)
        {
            throw new FileValidationException("Не передано ни одного файла");
        }

        if (files.Count > FileUploadLimits.MaxFileCount)
        {
            throw new FileValidationException($"Слишком много файлов (максимум {FileUploadLimits.MaxFileCount})");
        }

        long total = 0;
        foreach (var file in files)
        {
            if (file.Length > FileUploadLimits.MaxFileSizeBytes)
            {
                throw new FileValidationException($"Файл слишком большой: {file.FileName}");
            }
            total += file.Length;
        }

        if (total > FileUploadLimits.MaxTotalSizeBytes)
        {
            throw new FileValidationException("Суммарный размер файлов превышает лимит");
        }
    }
}
