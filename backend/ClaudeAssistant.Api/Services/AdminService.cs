using System.IO.Compression;
using System.Text.Json.Nodes;
using ClaudeAssistant.Api.Auth;
using ClaudeAssistant.Api.Data;
using ClaudeAssistant.Api.Hubs;
using ClaudeAssistant.Api.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace ClaudeAssistant.Api.Services;

// Общая логика админ-операций — используется и HTTP-контроллером (AdminController,
// для программного доступа), и веб-панелью (Blazor-компоненты вызывают эти методы
// напрямую, без HTTP-петли на себя же).
public class AdminService(AppDbContext db, IHubContext<UpdatesHub> hub)
{
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

    public async Task<List<string>> ListReleaseVersionsAsync()
    {
        var feedPath = Path.Combine(Program.ReleasesPath, "releases.win.json");
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

    // Убирает версию из releases.win.json (файлы пакетов не удаляет — можно откатить
    // обратно). Клиент откатится сам, только если у него включён AllowVersionDowngrade
    // (см. UpdateService на клиенте) — штатный флаг Velopack именно под этот сценарий.
    public async Task<RetractOutcome> RetractReleaseAsync(string version)
    {
        var feedPath = Path.Combine(Program.ReleasesPath, "releases.win.json");
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
