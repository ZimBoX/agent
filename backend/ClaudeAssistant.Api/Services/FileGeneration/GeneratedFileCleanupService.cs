using ClaudeAssistant.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace ClaudeAssistant.Api.Services.FileGeneration;

// Раз в сутки зачищает содержимое сгенерированных файлов старше 30 дней: сами байты
// (Content) обнуляются, а метаданные (имя, привязка к истории/чату) остаются — так
// в истории файл виден как «файл удалён», без возможности скачать. Хранить байты
// дольше нет смысла: свежий файл модель сгенерирует заново.
public class GeneratedFileCleanupService(IServiceScopeFactory scopeFactory, ILogger<GeneratedFileCleanupService> logger)
    : BackgroundService
{
    private static readonly TimeSpan Retention = TimeSpan.FromDays(30);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Небольшая задержка на старте, чтобы не конкурировать с применением миграций.
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PurgeAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Не удалось зачистить просроченные сгенерированные файлы");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task PurgeAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var cutoff = DateTimeOffset.UtcNow - Retention;
        var purged = await db.GeneratedFiles
            .Where(f => f.Content != null && f.CreatedAt < cutoff)
            .ExecuteUpdateAsync(s => s.SetProperty(f => f.Content, (byte[]?)null).SetProperty(f => f.SizeBytes, 0), cancellationToken);

        if (purged > 0)
        {
            logger.LogInformation("Зачищено просроченных сгенерированных файлов: {Count}", purged);
        }
    }
}
