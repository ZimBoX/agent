using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace ClaudeAssistant.Api.Hubs;

public static class UpdatesHubEvents
{
    public const string TokenRevoked = "TokenRevoked";
    public const string UpdateAvailable = "UpdateAvailable";
}

// Постоянное соединение клиента с сервером (раздел 9 плана): мгновенный разлогин при
// отзыве токена админом + (позже, Этап 7) уведомление о новом релизе. Авторизация —
// та же device-token схема, что и на обычных эндпоинтах; ClaimTypes.NameIdentifier
// (userId) используется SignalR как UserIdentifier по умолчанию, поэтому таргетировать
// пользователя можно через Clients.User(userId).
[Authorize]
public class UpdatesHub : Hub;
