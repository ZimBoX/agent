namespace ClaudeAssistant.Api.Models;

public class User
{
    public Guid Id { get; set; }
    public required string FullName { get; set; }
    public required string Role { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public List<Device> Devices { get; set; } = [];
    public List<RequestHistoryEntry> RequestHistory { get; set; } = [];
}
