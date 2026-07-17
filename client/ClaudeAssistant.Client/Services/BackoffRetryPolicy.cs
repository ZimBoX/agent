using Microsoft.AspNetCore.SignalR.Client;

namespace ClaudeAssistant.Client.Services;

// Экспоненциальный backoff: 1с → 2с → 4с ... максимум 30с (раздел 8.1 плана)
public class BackoffRetryPolicy : IRetryPolicy
{
    public TimeSpan? NextRetryDelay(RetryContext retryContext)
    {
        var seconds = Math.Min(30, Math.Pow(2, retryContext.PreviousRetryCount));
        return TimeSpan.FromSeconds(seconds);
    }
}
