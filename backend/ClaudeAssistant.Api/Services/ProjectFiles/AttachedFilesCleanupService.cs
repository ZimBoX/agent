namespace ClaudeAssistant.Api.Services.ProjectFiles;

// Периодически удаляет временные песочницы вложений старше ретеншена (см.
// AttachedFilesSandboxService.SweepExpired). Диалог живёт 1 ч, ретеншен с запасом.
public class AttachedFilesCleanupService(AttachedFilesSandboxService sandbox, ILogger<AttachedFilesCleanupService> logger)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                sandbox.SweepExpired();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Не удалось зачистить временные песочницы вложений");
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
}
