using System.IO.Compression;
using System.Text.Json.Nodes;
using ClaudeAssistant.Api.Auth;
using ClaudeAssistant.Api.Data;
using ClaudeAssistant.Api.Hubs;
using ClaudeAssistant.Api.Models;
using ClaudeAssistant.Api.Services.Pricing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace ClaudeAssistant.Api.Services;

// Общая логика админ-операций — используется и HTTP-контроллером (AdminController,
// для программного доступа), и веб-панелью (Blazor-компоненты вызывают эти методы
// напрямую, без HTTP-петли на себя же).
public class AdminService(AppDbContext db, IHubContext<UpdatesHub> hub, PricingService pricing)
{
    // Статистика использования по пользователю: запросов, токенов вход/выход,
    // стоимость в рублях (по ставкам компании) и фактическая стоимость от
    // агрегатора ($). Запрос = запись основного потока (execute + все его
    // продолжения одной строкой) ИЛИ один ответ модели в чате.
    public record UserUsageStats(
        Guid UserId,
        string FullName,
        string Role,
        int Requests,
        long InputTokens,
        long CacheReadTokens,
        long OutputTokens,
        decimal RubCost,
        double ProviderCostUsd
    );

    public record UsageStatsResult(List<UserUsageStats> Users, UserUsageStats Totals);

    // since == null — за всё время; иначе только записи с CreatedAt >= since
    // (день/неделя/месяц выбираются в веб-панели).
    public async Task<UsageStatsResult> GetUsageStatsAsync(DateTimeOffset? since = null)
    {
        // Основной поток: агрегируем по (пользователь, модель) — ставка зависит от модели.
        var requestAgg = await db.RequestHistory
            .Where(r => since == null || r.CreatedAt >= since)
            .GroupBy(r => new { r.UserId, r.Model })
            .Select(g => new
            {
                g.Key.UserId,
                g.Key.Model,
                Requests = g.Count(),
                Input = g.Sum(x => (long)x.InputTokens),
                CacheRead = g.Sum(x => (long)x.CacheReadTokens),
                Output = g.Sum(x => (long)x.OutputTokens),
                Cost = g.Sum(x => x.ProviderCostUsd),
            })
            .ToListAsync();

        // Чат: считаем ответы модели (IsUser=false), модель берём из диалога.
        var chatAgg = await db.ChatMessages
            .Where(m => !m.IsUser && (since == null || m.CreatedAt >= since))
            .GroupBy(m => new { m.Conversation!.UserId, m.Conversation.Model })
            .Select(g => new
            {
                g.Key.UserId,
                Model = (string?)g.Key.Model,
                Requests = g.Count(),
                Input = g.Sum(x => (long)x.InputTokens),
                CacheRead = g.Sum(x => (long)x.CacheReadTokens),
                Output = g.Sum(x => (long)x.OutputTokens),
                Cost = g.Sum(x => x.ProviderCostUsd),
            })
            .ToListAsync();

        var users = await db.Users.ToDictionaryAsync(u => u.Id);

        var perUser = new Dictionary<Guid, (int Req, long Inp, long Cr, long Outp, decimal Rub, double Usd)>();

        void Accumulate(Guid userId, string? model, int requests, long input, long cacheRead, long output, double usd)
        {
            var rub = pricing.RubCost(model, input, cacheRead, output);
            perUser.TryGetValue(userId, out var cur);
            perUser[userId] = (cur.Req + requests, cur.Inp + input, cur.Cr + cacheRead, cur.Outp + output, cur.Rub + rub, cur.Usd + usd);
        }

        foreach (var a in requestAgg)
        {
            Accumulate(a.UserId, a.Model, a.Requests, a.Input, a.CacheRead, a.Output, a.Cost);
        }
        foreach (var a in chatAgg)
        {
            Accumulate(a.UserId, a.Model, a.Requests, a.Input, a.CacheRead, a.Output, a.Cost);
        }

        var list = perUser
            .Select(kv =>
            {
                var user = users.GetValueOrDefault(kv.Key);
                var v = kv.Value;
                return new UserUsageStats(
                    kv.Key,
                    user?.FullName ?? "(удалён)",
                    user?.Role ?? "",
                    v.Req,
                    v.Inp,
                    v.Cr,
                    v.Outp,
                    v.Rub,
                    v.Usd
                );
            })
            .OrderByDescending(x => x.RubCost)
            .ToList();

        var totals = new UserUsageStats(
            Guid.Empty,
            "Итого",
            "",
            list.Sum(x => x.Requests),
            list.Sum(x => x.InputTokens),
            list.Sum(x => x.CacheReadTokens),
            list.Sum(x => x.OutputTokens),
            list.Sum(x => x.RubCost),
            list.Sum(x => x.ProviderCostUsd)
        );

        return new UsageStatsResult(list, totals);
    }

    public async Task<List<User>> ListUsersAsync() =>
        await db.Users.OrderByDescending(u => u.CreatedAt).ToListAsync();

    public record UserWithDeviceSummary(User User, int ActiveDeviceCount, DateTimeOffset? LastSeenAt);

    // Для списка сотрудников в веб-панели — чтобы найти "у кого сейчас активная
    // привязка" можно было прямо в списке, без захода в каждого сотрудника по
    // очереди (см. Этап 9 чеклиста — это и была реальная жалоба при тестировании).
    public async Task<List<UserWithDeviceSummary>> ListUsersWithDeviceSummaryAsync()
    {
        var users = await db.Users.OrderByDescending(u => u.CreatedAt).ToListAsync();

        var deviceStats = await db.Devices
            .Where(d => d.RevokedAt == null)
            .GroupBy(d => d.UserId)
            .Select(g => new { UserId = g.Key, Count = g.Count(), LastSeenAt = g.Max(d => d.LastSeenAt) })
            .ToListAsync();
        var statsByUserId = deviceStats.ToDictionary(s => s.UserId);

        return users
            .Select(u =>
                statsByUserId.TryGetValue(u.Id, out var stats)
                    ? new UserWithDeviceSummary(u, stats.Count, stats.LastSeenAt)
                    : new UserWithDeviceSummary(u, 0, null)
            )
            .ToList();
    }

    public async Task<User?> GetUserAsync(Guid userId) =>
        await db.Users.FindAsync(userId);

    public async Task<User> CreateUserAsync(string fullName, string role)
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            FullName = fullName,
            Role = role,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    public async Task<EnrollmentCode?> CreateEnrollmentCodeAsync(Guid userId)
    {
        var userExists = await db.Users.AnyAsync(u => u.Id == userId);
        if (!userExists)
        {
            return null;
        }

        var code = new EnrollmentCode
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Code = TokenHasher.GenerateEnrollmentCode(),
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15),
        };
        db.EnrollmentCodes.Add(code);
        await db.SaveChangesAsync();
        return code;
    }

    // Удаляет сотрудника целиком — устройства/коды привязки/история запросов
    // удаляются каскадом (см. AppDbContext.OnModelCreating). Живое устройство перед
    // удалением уведомляем через тот же канал, что и при обычном отзыве — иначе
    // клиент продолжит считать себя авторизованным, пока не столкнётся с ошибкой на
    // следующем запросе.
    public async Task<bool> DeleteUserAsync(Guid userId)
    {
        var user = await db.Users.FindAsync(userId);
        if (user is null)
        {
            return false;
        }

        db.Users.Remove(user);
        await db.SaveChangesAsync();

        await hub.Clients.User(userId.ToString()).SendAsync(UpdatesHubEvents.TokenRevoked);
        return true;
    }

    // Массовый отзыв — тот самый сценарий "1 ПК = 1 пользователь", когда на одном
    // тестовом устройстве накопились активные привязки от разных сотрудников:
    // не нужно отзывать по одной через ListDevicesAsync/RevokeDeviceAsync.
    public async Task<int> RevokeAllDevicesAsync(Guid userId)
    {
        var activeDevices = await db.Devices
            .Where(d => d.UserId == userId && d.RevokedAt == null)
            .ToListAsync();

        if (activeDevices.Count == 0)
        {
            return 0;
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var device in activeDevices)
        {
            device.RevokedAt = now;
        }
        await db.SaveChangesAsync();

        await hub.Clients.User(userId.ToString()).SendAsync(UpdatesHubEvents.TokenRevoked);
        return activeDevices.Count;
    }

    public async Task<List<Role>> ListRolesAsync() =>
        await db.Roles.OrderBy(r => r.Name).ToListAsync();

    public enum CreateRoleOutcome
    {
        Ok,
        AlreadyExists,
    }

    public async Task<(CreateRoleOutcome Outcome, Role? Role)> CreateRoleAsync(string name, bool isAdmin = false)
    {
        var exists = await db.Roles.AnyAsync(r => r.Name == name);
        if (exists)
        {
            return (CreateRoleOutcome.AlreadyExists, null);
        }

        var role = new Role { Id = Guid.NewGuid(), Name = name, IsAdmin = isAdmin, CreatedAt = DateTimeOffset.UtcNow };
        db.Roles.Add(role);
        await db.SaveChangesAsync();
        return (CreateRoleOutcome.Ok, role);
    }

    public async Task<bool> UpdateRoleIsAdminAsync(Guid roleId, bool isAdmin)
    {
        var role = await db.Roles.FindAsync(roleId);
        if (role is null)
        {
            return false;
        }

        role.IsAdmin = isAdmin;
        await db.SaveChangesAsync();
        return true;
    }

    // У уже существующих сотрудников (User.Role — просто строка, не внешний ключ,
    // см. Models/Role.cs) должность не трогаем. У шаблонов, наоборот, реальная
    // связь many-to-many — удаление роли отвяжет её от всех шаблонов, где она
    // стояла (каскадно удалятся только строки в TemplateRoles, не сами шаблоны).
    public async Task<bool> DeleteRoleAsync(Guid roleId)
    {
        var role = await db.Roles.FindAsync(roleId);
        if (role is null)
        {
            return false;
        }

        db.Roles.Remove(role);
        await db.SaveChangesAsync();
        return true;
    }

    // ===== Модели (управляемый список для селектора у админа в клиенте) =====

    public async Task<List<AiModel>> ListAiModelsAsync() =>
        await db.AiModels.OrderBy(m => m.SortOrder).ThenBy(m => m.DisplayName).ToListAsync();

    public async Task<AiModel> CreateAiModelAsync(string modelId, string displayName)
    {
        var maxOrder = await db.AiModels.Select(m => (int?)m.SortOrder).MaxAsync() ?? 0;
        var model = new AiModel
        {
            Id = Guid.NewGuid(),
            ModelId = modelId.Trim(),
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? modelId.Trim() : displayName.Trim(),
            SortOrder = maxOrder + 1,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.AiModels.Add(model);
        await db.SaveChangesAsync();
        return model;
    }

    public async Task<bool> DeleteAiModelAsync(Guid id)
    {
        var model = await db.AiModels.FindAsync(id);
        if (model is null)
        {
            return false;
        }
        db.AiModels.Remove(model);
        await db.SaveChangesAsync();
        return true;
    }

    // Разрешён ли этому ModelId выбор (есть ли он в управляемом списке) — сервер
    // принимает model-override от админа только для моделей из списка.
    public async Task<bool> IsKnownModelAsync(string modelId) =>
        await db.AiModels.AnyAsync(m => m.ModelId == modelId);

    // Роль (по имени, как в токене устройства) помечена админской? Единая точка —
    // и для показа селектора, и для авторизации model-override.
    public async Task<bool> IsAdminRoleAsync(string? roleName) =>
        roleName is not null && await db.Roles.AnyAsync(r => r.Name == roleName && r.IsAdmin);

    public async Task<List<Template>> ListTemplatesAsync() =>
        await db.Templates.Include(t => t.Roles).OrderBy(t => t.Title).ToListAsync();

    public async Task<Template?> GetTemplateAsync(Guid templateId) =>
        await db.Templates.Include(t => t.Roles).FirstOrDefaultAsync(t => t.Id == templateId);

    public async Task<Template> CreateTemplateAsync(string title, List<Guid> roleIds, string systemPrompt, string model, bool allowFileSearch = false)
    {
        var roles = await db.Roles.Where(r => roleIds.Contains(r.Id)).ToListAsync();
        var template = new Template
        {
            Id = Guid.NewGuid(),
            Title = title,
            Roles = roles,
            SystemPrompt = systemPrompt,
            Model = model,
            AllowFileSearch = allowFileSearch,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Templates.Add(template);
        await db.SaveChangesAsync();
        return template;
    }

    public async Task<bool> UpdateTemplateAsync(Guid templateId, string title, List<Guid> roleIds, string systemPrompt, string model, bool allowFileSearch = false)
    {
        var template = await db.Templates.Include(t => t.Roles).FirstOrDefaultAsync(t => t.Id == templateId);
        if (template is null)
        {
            return false;
        }

        var roles = await db.Roles.Where(r => roleIds.Contains(r.Id)).ToListAsync();

        template.Title = title;
        template.Roles = roles;
        template.SystemPrompt = systemPrompt;
        template.Model = model;
        template.AllowFileSearch = allowFileSearch;
        template.Version++;
        await db.SaveChangesAsync();
        return true;
    }

    public enum DeleteTemplateOutcome
    {
        Ok,
        NotFound,

        // История запросов ссылается на шаблон через Restrict-связь (сознательно —
        // нельзя, чтобы прошлый запрос "потерял" информацию о том, каким шаблоном
        // он был выполнен), поэтому удалить шаблон, который уже использовался,
        // нельзя вообще — только если история пуста.
        InUse,
    }

    public async Task<DeleteTemplateOutcome> DeleteTemplateAsync(Guid templateId)
    {
        var template = await db.Templates.FindAsync(templateId);
        if (template is null)
        {
            return DeleteTemplateOutcome.NotFound;
        }

        var inUse = await db.RequestHistory.AnyAsync(h => h.TemplateId == templateId);
        if (inUse)
        {
            return DeleteTemplateOutcome.InUse;
        }

        db.Templates.Remove(template);
        await db.SaveChangesAsync();
        return DeleteTemplateOutcome.Ok;
    }

    public async Task<List<Device>> ListDevicesAsync(Guid userId) =>
        await db.Devices
            .Where(d => d.UserId == userId)
            .OrderByDescending(d => d.CreatedAt)
            .ToListAsync();

    public async Task<bool> RevokeDeviceAsync(Guid deviceId)
    {
        var device = await db.Devices.FindAsync(deviceId);
        if (device is null)
        {
            return false;
        }

        device.RevokedAt ??= DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        // Мгновенный разлогин через постоянное соединение — см. DeviceTokenAuthenticationHandler.
        await hub.Clients.User(device.UserId.ToString()).SendAsync(UpdatesHubEvents.TokenRevoked);
        return true;
    }

    public async Task<List<string>> ListReleaseVersionsAsync(string channel = "win")
    {
        var feedPath = Path.Combine(Program.ReleasesPath, $"releases.{channel}.json");
        if (!File.Exists(feedPath))
        {
            return [];
        }

        var root = JsonNode.Parse(await File.ReadAllTextAsync(feedPath))!.AsObject();
        return root["Assets"]!.AsArray()
            .Where(a => (string?)a!["Type"] == "Full")
            .Select(a => (string)a!["Version"]!)
            .OrderByDescending(Version.Parse)
            .ToList();
    }

    // Принимает zip-архив с содержимым папки, которую производит `vpk pack` (Setup.exe,
    // RELEASES, *.nupkg), распаковывает в release-фид и уведомляет все подключённые
    // клиенты. Файлы не удаляются перед распаковкой — новые версии добавляются к
    // старым, это нужно Velopack для дельта-обновлений.
    public async Task UploadReleaseAsync(Stream zipStream)
    {
        var tempZipPath = Path.GetTempFileName();
        try
        {
            await using (var stream = File.Create(tempZipPath))
            {
                await zipStream.CopyToAsync(stream);
            }

            ZipFile.ExtractToDirectory(tempZipPath, Program.ReleasesPath, overwriteFiles: true);
        }
        finally
        {
            File.Delete(tempZipPath);
        }

        await hub.Clients.All.SendAsync(UpdatesHubEvents.UpdateAvailable);
    }

    public enum RetractOutcome
    {
        Ok,
        VersionNotFound,
        LastRemainingVersion,
    }

    // Убирает версию из releases.<channel>.json (файлы пакетов не удаляет — можно
    // откатить обратно). Клиент откатится сам, только если у него включён
    // AllowVersionDowngrade (см. UpdateService на клиенте) — штатный флаг Velopack
    // именно под этот сценарий. Каждый канал (win/beta/...) — отдельный независимый
    // фид: "последняя оставшаяся версия" считается в пределах только своего канала.
    public async Task<RetractOutcome> RetractReleaseAsync(string version, string channel = "win")
    {
        var feedPath = Path.Combine(Program.ReleasesPath, $"releases.{channel}.json");
        if (!File.Exists(feedPath))
        {
            return RetractOutcome.VersionNotFound;
        }

        var root = JsonNode.Parse(await File.ReadAllTextAsync(feedPath))!.AsObject();
        var assets = root["Assets"]!.AsArray();

        var toRemove = assets.Where(a => (string?)a!["Version"] == version).ToList();
        if (toRemove.Count == 0)
        {
            return RetractOutcome.VersionNotFound;
        }

        var hasOtherFull = assets.Any(a => (string?)a!["Version"] != version && (string?)a!["Type"] == "Full");
        if (!hasOtherFull)
        {
            return RetractOutcome.LastRemainingVersion;
        }

        foreach (var item in toRemove)
        {
            assets.Remove(item);
        }

        await File.WriteAllTextAsync(feedPath, root.ToJsonString());
        await hub.Clients.All.SendAsync(UpdatesHubEvents.UpdateAvailable);

        return RetractOutcome.Ok;
    }
}
