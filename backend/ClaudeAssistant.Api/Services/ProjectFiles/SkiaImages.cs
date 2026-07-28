using SkiaSharp;

namespace ClaudeAssistant.Api.Services.ProjectFiles;

// Общие помощники по картинкам для отправки vision-модели.
public static class SkiaImages
{
    // Кодирует в JPEG. JPEG-энкодер SkiaSharp не поддерживает часть цветовых типов
    // (Gray8 и др.) и молча падает в PNG-фолбэк — поэтому предварительно перерисовываем
    // в непрозрачный Rgba8888. Реальный формат всё равно определяем по байтам
    // (DetectMediaType) на случай отсутствия JPEG-энкодера в сборке.
    public static byte[] EncodeJpeg(SKBitmap bitmap, int quality)
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
        using var png = image.Encode();
        return png.ToArray();
    }

    // Тип картинки по «магическим» байтам — чтобы пометить ImageBlockParam верно,
    // независимо от того, что реально закодировал SkiaSharp.
    public static string DetectMediaType(byte[] image) =>
        image.Length >= 3 && image[0] == 0xFF && image[1] == 0xD8 && image[2] == 0xFF ? "image/jpeg" : "image/png";
}
