using ClaudeAssistant.Api.Auth;
using ClaudeAssistant.Api.Dtos;
using ClaudeAssistant.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeAssistant.Api.Controllers;

// Доступен только вошедшим в веб-панель админа (см. AdminAuthDefaults) — раньше был
// вообще без auth, закрыто в рамках Этапа 9. Бизнес-логика вынесена в AdminService,
// чтобы Blazor-страницы могли вызывать её напрямую, без HTTP-петли на этот же процесс.
[ApiController]
[Route("api/admin")]
[Authorize(AuthenticationSchemes = AdminAuthDefaults.SchemeName, Policy = AdminAuthDefaults.PolicyName)]
public class AdminController(AdminService adminService) : ControllerBase
{
    [HttpPost("users")]
    public async Task<ActionResult<UserResponse>> CreateUser(CreateUserRequest request)
    {
        var user = await adminService.CreateUserAsync(request.FullName, request.Role);
        return Ok(new UserResponse(user.Id, user.FullName, user.Role, user.CreatedAt));
    }

    [HttpGet("users")]
    public async Task<ActionResult<List<UserResponse>>> ListUsers()
    {
        var users = await adminService.ListUsersAsync();
        return Ok(users.Select(u => new UserResponse(u.Id, u.FullName, u.Role, u.CreatedAt)).ToList());
    }

    [HttpPost("users/{userId:guid}/enrollment-codes")]
    public async Task<ActionResult<EnrollmentCodeResponse>> CreateEnrollmentCode(Guid userId)
    {
        var code = await adminService.CreateEnrollmentCodeAsync(userId);
        if (code is null)
        {
            return NotFound();
        }

        return Ok(new EnrollmentCodeResponse(code.Code, code.ExpiresAt));
    }

    [HttpGet("users/{userId:guid}/devices")]
    public async Task<ActionResult<List<DeviceResponse>>> ListDevices(Guid userId)
    {
        var devices = await adminService.ListDevicesAsync(userId);
        return Ok(devices.Select(d => new DeviceResponse(d.Id, d.UserId, d.CreatedAt, d.RevokedAt, d.LastSeenAt)).ToList());
    }

    [HttpDelete("users/{userId:guid}")]
    public async Task<IActionResult> DeleteUser(Guid userId)
    {
        var deleted = await adminService.DeleteUserAsync(userId);
        return deleted ? NoContent() : NotFound();
    }

    [HttpPost("users/{userId:guid}/devices/revoke-all")]
    public async Task<IActionResult> RevokeAllDevices(Guid userId)
    {
        await adminService.RevokeAllDevicesAsync(userId);
        return NoContent();
    }

    [HttpPost("templates")]
    public async Task<ActionResult<TemplateResponse>> CreateTemplate(CreateTemplateRequest request)
    {
        var template = await adminService.CreateTemplateAsync(request.Title, request.RoleIds, request.SystemPrompt, request.Model, request.AllowFileSearch);
        return Ok(ToTemplateResponse(template));
    }

    [HttpGet("templates")]
    public async Task<ActionResult<List<TemplateResponse>>> ListTemplates()
    {
        var templates = await adminService.ListTemplatesAsync();
        return Ok(templates.Select(ToTemplateResponse).ToList());
    }

    [HttpPut("templates/{templateId:guid}")]
    public async Task<IActionResult> UpdateTemplate(Guid templateId, UpdateTemplateRequest request)
    {
        var updated = await adminService.UpdateTemplateAsync(templateId, request.Title, request.RoleIds, request.SystemPrompt, request.Model, request.AllowFileSearch);
        return updated ? NoContent() : NotFound();
    }

    [HttpDelete("templates/{templateId:guid}")]
    public async Task<IActionResult> DeleteTemplate(Guid templateId)
    {
        var outcome = await adminService.DeleteTemplateAsync(templateId);
        return outcome switch
        {
            AdminService.DeleteTemplateOutcome.Ok => NoContent(),
            AdminService.DeleteTemplateOutcome.NotFound => NotFound(),
            AdminService.DeleteTemplateOutcome.InUse => BadRequest(
                new ErrorResponse("Шаблон уже использовался в запросах — удаление недоступно")
            ),
            _ => throw new ArgumentOutOfRangeException(),
        };
    }

    private static TemplateResponse ToTemplateResponse(Models.Template t) =>
        new(t.Id, t.Title, t.Roles.Select(r => new RoleResponse(r.Id, r.Name, r.IsAdmin)).ToList(), t.SystemPrompt, t.Model, t.Version, t.AllowFileSearch);

    [HttpGet("roles")]
    public async Task<ActionResult<List<RoleResponse>>> ListRoles()
    {
        var roles = await adminService.ListRolesAsync();
        return Ok(roles.Select(r => new RoleResponse(r.Id, r.Name, r.IsAdmin)).ToList());
    }

    [HttpPost("roles")]
    public async Task<ActionResult<RoleResponse>> CreateRole(CreateRoleRequest request)
    {
        var (outcome, role) = await adminService.CreateRoleAsync(request.Name, request.IsAdmin);
        return outcome switch
        {
            AdminService.CreateRoleOutcome.Ok => Ok(new RoleResponse(role!.Id, role.Name, role.IsAdmin)),
            AdminService.CreateRoleOutcome.AlreadyExists => BadRequest(new ErrorResponse("Такая должность уже есть")),
            _ => throw new ArgumentOutOfRangeException(),
        };
    }

    [HttpDelete("roles/{roleId:guid}")]
    public async Task<IActionResult> DeleteRole(Guid roleId)
    {
        var deleted = await adminService.DeleteRoleAsync(roleId);
        return deleted ? NoContent() : NotFound();
    }

    [HttpPut("roles/{roleId:guid}/is-admin")]
    public async Task<IActionResult> UpdateRoleIsAdmin(Guid roleId, [FromBody] bool isAdmin)
    {
        var updated = await adminService.UpdateRoleIsAdminAsync(roleId, isAdmin);
        return updated ? NoContent() : NotFound();
    }

    [HttpPost("devices/{deviceId:guid}/revoke")]
    public async Task<IActionResult> RevokeDevice(Guid deviceId)
    {
        var revoked = await adminService.RevokeDeviceAsync(deviceId);
        return revoked ? NoContent() : NotFound();
    }

    // Обычная HTML-форма (не Blazor-компонент) на странице /admin/releases постит
    // сюда напрямую — так загрузка 100+ МБ архива идёт обычным HTTP-запросом браузера,
    // а не через SignalR-канал Blazor Server (у которого куда более скромные лимиты
    // на размер сообщения).
    [HttpPost("releases/upload")]
    [RequestSizeLimit(1024L * 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = 1024L * 1024 * 1024)]
    public async Task<IActionResult> UploadRelease(IFormFile file)
    {
        if (file.Length == 0)
        {
            return BadRequest(new ErrorResponse("Пустой файл"));
        }

        await using var stream = file.OpenReadStream();
        await adminService.UploadReleaseAsync(stream);

        return Redirect("/admin/releases");
    }

    [HttpGet("releases")]
    public async Task<ActionResult<List<string>>> ListReleases([FromQuery] string channel = "win") =>
        Ok(await adminService.ListReleaseVersionsAsync(channel));

    [HttpPost("releases/{version}/retract")]
    public async Task<IActionResult> RetractRelease(string version, [FromQuery] string channel = "win")
    {
        var outcome = await adminService.RetractReleaseAsync(version, channel);
        return outcome switch
        {
            AdminService.RetractOutcome.Ok => NoContent(),
            AdminService.RetractOutcome.VersionNotFound => NotFound(new ErrorResponse($"Версия {version} не найдена в фиде")),
            AdminService.RetractOutcome.LastRemainingVersion => BadRequest(
                new ErrorResponse("Нельзя отозвать единственную оставшуюся версию")
            ),
            _ => throw new ArgumentOutOfRangeException(),
        };
    }
}
