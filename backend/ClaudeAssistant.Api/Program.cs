using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using ClaudeAssistant.Api.Auth;
using ClaudeAssistant.Api.Components;
using ClaudeAssistant.Api.Data;
using ClaudeAssistant.Api.Hubs;
using ClaudeAssistant.Api.Services;
using ClaudeAssistant.Api.Services.AiCompletion;
using ClaudeAssistant.Api.Services.FileTextExtraction;
using ClaudeAssistant.Api.Services.ProjectFiles;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Файловый лог — раньше единственным способом увидеть полный текст/стектрейс
// ошибки было вручную перенаправлять вывод консоли в файл при запуске (`dotnet
// run > log.txt`), то есть лог существовал только пока процесс запускал именно
// разработчик именно так. Пишем в %LocalAppData%, а не в bin/obj — те периодически
// полностью стираются при чистых пересборках (см. чеклист, баг с .pri), и лог
// вместе с ними тоже стирался бы.
builder.Host.UseSerilog(
    (_, config) =>
        config
            .WriteTo.Console()
            .WriteTo.File(
                Path.Combine(Program.LogsPath, "log-.txt"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14
            )
);

// Add services to the container.

builder.Services.AddControllers();
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Default")));

const string SmartSchemeName = "Smart";
builder.Services
    // "Smart" — policy-схема без собственной логики аутентификации, только решает
    // (по пути запроса), какую из двух настоящих схем форвардить как эффективный
    // default. Нужна из-за того, что Blazor-компоненты читают HttpContext.User,
    // который заполняет UseAuthentication() ИМЕННО default-схемой — в отличие от
    // MVC-контроллеров, [Authorize(AuthenticationSchemes = ...)] на самом
    // компоненте это не переопределяет.
    .AddAuthentication(SmartSchemeName)
    .AddPolicyScheme(
        SmartSchemeName,
        SmartSchemeName,
        options =>
        {
            options.ForwardDefaultSelector = context =>
                context.Request.Path.StartsWithSegments("/api/admin")
                || !context.Request.Path.StartsWithSegments("/api")
                    && !context.Request.Path.StartsWithSegments("/hubs")
                    ? AdminAuthDefaults.SchemeName
                    : DeviceTokenDefaults.SchemeName;
        }
    )
    .AddScheme<AuthenticationSchemeOptions, DeviceTokenAuthenticationHandler>(
        DeviceTokenDefaults.SchemeName,
        _ => { }
    )
    // Отдельная cookie-схема для веб-панели админа (Этап 9) — не смешивается с
    // device-token схемой клиента.
    .AddCookie(
        AdminAuthDefaults.SchemeName,
        options =>
        {
            options.LoginPath = "/admin/login";
            options.Cookie.Name = "ClaudeAssistant.Admin";
            options.ExpireTimeSpan = TimeSpan.FromHours(12);
        }
    );
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(
        AdminAuthDefaults.PolicyName,
        policy => policy.AddAuthenticationSchemes(AdminAuthDefaults.SchemeName).RequireAuthenticatedUser()
    );
});
builder.Services.AddSignalR();

builder.Services.AddScoped<FileTextExtractor>();
builder.Services.AddScoped<MultiFileExtractionService>();
builder.Services.AddScoped<ChatCompletionService>();
builder.Services.AddScoped<AdminService>();
builder.Services.AddScoped<SandboxedFileAccessService>();
// Продолжаемые диалоги живут в памяти (окно = TTL кэша Anthropic, 1 час) —
// стор Singleton, чтобы состояние переживало отдельные HTTP-запросы.
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<ConversationStore>();

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddCascadingAuthenticationState();

var app = builder.Build();

// Применяем миграции EF Core при старте. На сервере бэкенд едет в Docker-контейнере,
// где нет `dotnet ef` под рукой, а Postgres поднимается пустым контейнером — без этого
// шага схемы бы не было и всё падало бы 500. Идемпотентно: EF накатывает только
// недостающие миграции, на уже актуальной БД (в т.ч. локальной dev) — no-op.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();
}

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

// Release-фид для Velopack (Этап 7) — просто статические файлы, которые `vpk pack`
// кладёт в releases-папку (Setup.exe, RELEASES, *.nupkg). Не требует своего API —
// клиентский UpdateManager (SimpleWebSource) ходит сюда напрямую по HTTP.
Directory.CreateDirectory(Program.ReleasesPath);
// UseStaticFiles по умолчанию не отдаёт файлы с нераспознанным расширением
// (ServeUnknownFileTypes = false) — без явного маппинга .nupkg/.exe отдавались
// как 404, из-за чего UpdateManager не мог скачать ни дельту, ни full-пакет.
var releasesContentTypes = new FileExtensionContentTypeProvider();
releasesContentTypes.Mappings[".nupkg"] = "application/octet-stream";
releasesContentTypes.Mappings[".exe"] = "application/octet-stream";
app.UseStaticFiles(
    new StaticFileOptions
    {
        FileProvider = new PhysicalFileProvider(Program.ReleasesPath),
        RequestPath = "/releases",
        ContentTypeProvider = releasesContentTypes,
    }
);

app.MapControllers();
app.MapHub<UpdatesHub>("/hubs/updates");

// Логин веб-панели админа — обычная (не Blazor) форма и POST-обработчик. Сделано
// осознанно вне интерактивных Razor-компонентов: SignInAsync пишет cookie в ответ,
// а после того как компонент начал стримить содержимое через живое SignalR-соединение
// (circuit), заголовки/cookie отправить уже нельзя — поэтому именно логин живёт
// на уровне обычного HTTP-эндпоинта, а не страницы.
var adminUsername = Environment.GetEnvironmentVariable("ADMIN_USERNAME") ?? "admin";
var adminPassword = Environment.GetEnvironmentVariable("ADMIN_PASSWORD") ?? "admin";
if (Environment.GetEnvironmentVariable("ADMIN_PASSWORD") is null)
{
    app.Logger.LogWarning(
        "ADMIN_PASSWORD не задан — используется пароль по умолчанию 'admin'. " +
            "Задайте переменную окружения ADMIN_PASSWORD перед выходом за пределы локальной разработки."
    );
}

app.MapGet(
    "/admin/login",
    (HttpContext ctx, Microsoft.AspNetCore.Antiforgery.IAntiforgery antiforgery, string? error) =>
    {
        var tokens = antiforgery.GetAndStoreTokens(ctx);
        return Results.Content(
            AdminLoginPage.Render(error is not null, tokens.FormFieldName, tokens.RequestToken!),
            "text/html; charset=utf-8"
        );
    }
);

app.MapPost(
    "/admin/login",
    async (HttpContext ctx, [FromForm] string username, [FromForm] string password) =>
    {
        var usernameOk = CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(username),
            Encoding.UTF8.GetBytes(adminUsername)
        );
        var passwordOk = CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(password),
            Encoding.UTF8.GetBytes(adminPassword)
        );

        if (!usernameOk || !passwordOk)
        {
            return Results.Redirect("/admin/login?error=1");
        }

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, username)],
            AdminAuthDefaults.SchemeName
        );
        await ctx.SignInAsync(AdminAuthDefaults.SchemeName, new ClaimsPrincipal(identity));
        return Results.Redirect("/admin");
    }
);

app.MapPost(
    "/admin/logout",
    async (HttpContext ctx) =>
    {
        await ctx.SignOutAsync(AdminAuthDefaults.SchemeName);
        return Results.Redirect("/admin/login");
    }
);

app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();

public partial class Program
{
    // Тот же путь, что и у app.UseStaticFiles выше — используется эндпоинтом загрузки
    // билда (AdminController), чтобы оба указывали ровно на одну и ту же папку.
    public static string ReleasesPath { get; } = Path.Combine(AppContext.BaseDirectory, "releases");

    public static string LogsPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClaudeAssistant",
        "backend-logs"
    );
}
