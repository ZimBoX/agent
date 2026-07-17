namespace ClaudeAssistant.Api.Dtos;

public record CreateUserRequest(string FullName, string Role);

public record UserResponse(Guid Id, string FullName, string Role, DateTimeOffset CreatedAt);

public record EnrollmentCodeResponse(string Code, DateTimeOffset ExpiresAt);

public record DeviceResponse(
    Guid Id,
    Guid UserId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? RevokedAt,
    DateTimeOffset? LastSeenAt
);

public record CreateTemplateRequest(string Title, List<Guid> RoleIds, string SystemPrompt, string Model, bool AllowFileSearch = false);

public record UpdateTemplateRequest(string Title, List<Guid> RoleIds, string SystemPrompt, string Model, bool AllowFileSearch = false);

public record TemplateResponse(Guid Id, string Title, List<RoleResponse> Roles, string SystemPrompt, string Model, int Version, bool AllowFileSearch);

public record CreateRoleRequest(string Name, bool IsAdmin = false);

public record RoleResponse(Guid Id, string Name, bool IsAdmin = false);
