namespace ClaudeAssistant.Api.Dtos;

public record BindDeviceRequest(string Code, string Token);

public record BindDeviceResponse(Guid DeviceId, string UserFullName, string UserRole);
