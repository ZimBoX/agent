namespace ClaudeAssistant.Api.Components;

// Простая статическая HTML-страница логина (не Blazor-компонент) — см. комментарий
// в Program.cs о том, почему логин не может быть интерактивным Razor-компонентом.
public static class AdminLoginPage
{
    private const string Shell = """
        <!DOCTYPE html>
        <html lang="ru">
        <head>
            <meta charset="utf-8" />
            <title>Claude Assistant — вход</title>
            <meta name="viewport" content="width=device-width, initial-scale=1" />
            <style>
                body { font-family: system-ui, sans-serif; background: #1e1e1e; color: #e0e0e0;
                        display: flex; align-items: center; justify-content: center; height: 100vh; margin: 0; }
                form { background: #2a2a2a; padding: 2rem; border-radius: 8px; min-width: 280px; }
                h1 { font-size: 1.2rem; margin: 0 0 1.2rem; }
                label { display: block; margin-bottom: 0.3rem; font-size: 0.9rem; color: #aaa; }
                input { width: 100%; padding: 0.5rem; margin-bottom: 1rem; box-sizing: border-box;
                         background: #1e1e1e; border: 1px solid #444; color: #e0e0e0; border-radius: 4px; }
                button { width: 100%; padding: 0.6rem; background: #6b4ce6; color: white; border: none;
                          border-radius: 4px; cursor: pointer; font-size: 1rem; }
                button:hover { background: #7d5ff0; }
                .error { color: #f28b82; font-size: 0.9rem; margin-bottom: 1rem; }
            </style>
        </head>
        <body>
            <form method="post" action="/admin/login">
                <h1>Claude Assistant — панель админа</h1>
                {{ERROR}}
                <input type="hidden" name="{{TOKEN_FIELD}}" value="{{TOKEN_VALUE}}" />
                <label for="username">Логин</label>
                <input type="text" id="username" name="username" autofocus required />
                <label for="password">Пароль</label>
                <input type="password" id="password" name="password" required />
                <button type="submit">Войти</button>
            </form>
        </body>
        </html>
        """;

    // antiforgery-токен нужен, потому что для minimal API с form-параметрами ASP.NET
    // Core 8 автоматически требует его (см. UseAntiforgery() в Program.cs) — без токена
    // POST /admin/login падал 400 (AntiforgeryValidationException).
    public static string Render(bool showError, string tokenFieldName, string tokenValue) =>
        Shell.Replace("{{ERROR}}", showError ? "<div class=\"error\">Неверный логин или пароль</div>" : "")
            .Replace("{{TOKEN_FIELD}}", tokenFieldName)
            .Replace("{{TOKEN_VALUE}}", tokenValue);
}
