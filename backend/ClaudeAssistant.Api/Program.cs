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
builder.Services.AddScoped<ChatService>();
builder.Services.AddScoped<AdminService>();
builder.Services.AddScoped<SandboxedFileAccessService>();
// Рендер PDF-чертежей в картинки для vision + дисковый кэш результатов (без состояния — Singleton).
builder.Services.AddSingleton<ClaudeAssistant.Api.Services.ProjectFiles.RenderCacheService>();
builder.Services.AddSingleton<ClaudeAssistant.Api.Services.ProjectFiles.DrawingRenderService>();
// Большие пачки вложений раскладываются во временную серверную песочницу и
// читаются инструментами (по частям + зум) вместо инлайна; фоновый свип чистит их.
builder.Services.AddSingleton<ClaudeAssistant.Api.Services.ProjectFiles.AttachedFilesSandboxService>();
builder.Services.AddHostedService<ClaudeAssistant.Api.Services.ProjectFiles.AttachedFilesCleanupService>();
// Продолжаемые диалоги живут в памяти (окно = TTL кэша Anthropic, 1 час) —
// стор Singleton, чтобы состояние переживало отдельные HTTP-запросы.
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<ConversationStore>();
// Генерация файлов моделью (create_file): сервис без состояния (Singleton) +
// эфемерное хранилище результатов в памяти (Singleton, TTL 1 час).
builder.Services.AddSingleton<ClaudeAssistant.Api.Services.FileGeneration.DocumentGenerationService>();
// Расчёт стоимости запросов в рублях по токенам (статистика в админке).
builder.Services.AddSingleton<ClaudeAssistant.Api.Services.Pricing.PricingService>();
// Фоновая зачистка сгенерированных файлов старше 30 дней.
builder.Services.AddHostedService<ClaudeAssistant.Api.Services.FileGeneration.GeneratedFileCleanupService>();

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

// Публичная (без логина) страница со списком релизов и ссылкой на установщик —
// раздельно по каналам (релиз `win` / бета). Читает releases.<channel>.json из
// релиз-фида; установщик канала — Install-ClaudeAssistant-<channel>.exe рядом в
// /releases (если загружен). Пользователи заходят сюда, чтобы скачать клиент.
app.MapGet(
    "/downloads",
    () =>
    {
        static List<(string Version, long Size)> ReadFull(string feedPath)
        {
            if (!File.Exists(feedPath))
            {
                return [];
            }

            var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(feedPath))!.AsObject();
            return root["Assets"]!
                .AsArray()
                .Where(a => (string?)a!["Type"] == "Full")
                .Select(a => ((string)a!["Version"]!, (long?)a!["Size"] ?? 0L))
                .OrderByDescending(x => Version.Parse(x.Item1))
                .ToList();
        }

        var sb = new StringBuilder();
        sb.Append(
            """
            <!doctype html><html lang="ru"><head><meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Claude Assistant — загрузки</title><style>
            :root{color-scheme:light dark}
            body{font-family:system-ui,Segoe UI,Roboto,sans-serif;max-width:760px;margin:2.5rem auto;padding:0 1.25rem;line-height:1.5}
            h1{font-size:1.6rem;margin:0 0 .25rem}
            .sub{opacity:.65;margin:0 0 2rem}
            section{border:1px solid color-mix(in srgb,currentColor 15%,transparent);border-radius:14px;padding:1.25rem 1.5rem;margin:0 0 1.25rem}
            h2{font-size:1.15rem;margin:0 0 .75rem;display:flex;align-items:center;gap:.6rem}
            .badge{font-size:.72rem;font-weight:600;padding:.15rem .5rem;border-radius:999px;background:color-mix(in srgb,currentColor 12%,transparent)}
            .btn{display:inline-block;background:#2563eb;color:#fff;text-decoration:none;padding:.6rem 1.1rem;border-radius:10px;font-weight:600;margin:.25rem 0 1rem}
            .btn:hover{background:#1d4ed8}
            .empty{opacity:.6;font-style:italic}
            table{width:100%;border-collapse:collapse;font-size:.92rem}
            th,td{text-align:left;padding:.4rem .5rem;border-bottom:1px solid color-mix(in srgb,currentColor 10%,transparent)}
            th{opacity:.6;font-weight:600}
            tr:last-child td{border-bottom:none}
            </style></head><body>
            <h1>Claude Assistant</h1><p class="sub">Установщик и список версий</p>
            """
        );

        foreach (var (ch, title) in new[] { ("win", "Релиз"), ("beta", "Бета") })
        {
            var releases = ReadFull(Path.Combine(Program.ReleasesPath, $"releases.{ch}.json"));
            var installer = $"Install-ClaudeAssistant-{ch}.exe";
            var installerExists = File.Exists(Path.Combine(Program.ReleasesPath, installer));

            sb.Append($"<section><h2>{title} <span class=\"badge\">{ch}</span></h2>");

            if (releases.Count == 0)
            {
                sb.Append("<p class=\"empty\">Пока нет версий в этом канале.</p></section>");
                continue;
            }

            if (installerExists)
            {
                sb.Append(
                    $"<a class=\"btn\" href=\"/releases/{installer}\">Скачать установщик · v{releases[0].Version}</a>"
                );
            }
            else
            {
                sb.Append("<p class=\"empty\">Установщик для этого канала ещё не загружен.</p>");
            }

            sb.Append("<table><thead><tr><th>Версия</th><th>Размер</th></tr></thead><tbody>");
            foreach (var (v, size) in releases)
            {
                sb.Append($"<tr><td>{v}</td><td>{size / 1024.0 / 1024.0:0.0} МБ</td></tr>");
            }
            sb.Append("</tbody></table></section>");
        }

        sb.Append("</body></html>");
        return Results.Content(sb.ToString(), "text/html; charset=utf-8");
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
