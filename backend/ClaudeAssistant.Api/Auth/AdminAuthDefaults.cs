namespace ClaudeAssistant.Api.Auth;

// Отдельная auth-схема для веб-панели админа — cookie-based логин одной учётной
// записью, никак не связана с device-token схемой, которой авторизуется клиент.
public static class AdminAuthDefaults
{
    public const string SchemeName = "AdminAuth";
    public const string PolicyName = "AdminOnly";
}
