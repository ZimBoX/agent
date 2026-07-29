using System.Security.Claims;
using ClaudeAssistant.Api.Dtos;
using ClaudeAssistant.Api.Services;
using ClaudeAssistant.Api.Services.FileTextExtraction;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeAssistant.Api.Controllers;

[ApiController]
[Route("api/chat")]
[Authorize]
public class ChatController(ChatService chatService, MultiFileExtractionService extractionService, AdminService adminService) : ControllerBase
{
    private Guid UserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    // Отправить сообщение в чат (мультипарт — можно приложить файлы). Ответ
    // стримится по токенам в SignalR (ChatToken), а тут возвращается целиком по
    // завершении — авторитетный текст + conversationId/title.
    [HttpPost("send")]
    [RequestSizeLimit(FileUploadLimits.MaxTotalSizeBytes)]
    public async Task<ActionResult<ChatSendResponse>> Send(
        [FromForm] Guid? conversationId,
        [FromForm] string? text,
        // Override модели — только от админа и только из списка (иначе игнор).
        // Действует на НОВЫЙ диалог; существующий сохраняет свою модель.
        [FromForm(Name = "model")] string? modelOverride,
        List<IFormFile> files,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(text) && files.Count == 0)
        {
            return BadRequest(new { error = "Пустое сообщение" });
        }

        string? effectiveModel = null;
        if (!string.IsNullOrWhiteSpace(modelOverride))
        {
            var role = User.FindFirstValue(ClaimTypes.Role);
            if (await adminService.IsAdminRoleAsync(role) && await adminService.IsKnownModelAsync(modelOverride))
            {
                effectiveModel = modelOverride;
            }
        }

        string? combinedFileText = null;
        IReadOnlyList<byte[]> images = [];
        string? fileNames = null;
        if (files.Count > 0)
        {
            try
            {
                var extraction = await extractionService.ExtractAndCombineAsync(files);
                combinedFileText = extraction.CombinedText;
                images = extraction.Images;
                fileNames = string.Join(", ", files.Select(f => f.FileName));
            }
            catch (FileValidationException ex)
            {
                return BadRequest(new { error = ex.Message });
            }
        }

        var response = await chatService.SendAsync(
            UserId,
            conversationId,
            (text ?? string.Empty).Trim(),
            combinedFileText,
            images,
            fileNames,
            effectiveModel,
            cancellationToken
        );
        return Ok(response);
    }

    [HttpGet("conversations")]
    public async Task<ActionResult<List<ChatConversationDto>>> ListConversations(CancellationToken cancellationToken) =>
        Ok(await chatService.ListConversationsAsync(UserId, cancellationToken));

    [HttpGet("conversations/{conversationId:guid}/messages")]
    public async Task<ActionResult<List<ChatMessageDto>>> GetMessages(Guid conversationId, CancellationToken cancellationToken)
    {
        var messages = await chatService.GetMessagesAsync(UserId, conversationId, cancellationToken);
        return messages is null ? NotFound() : Ok(messages);
    }

    [HttpDelete("conversations/{conversationId:guid}")]
    public async Task<IActionResult> DeleteConversation(Guid conversationId, CancellationToken cancellationToken)
    {
        var ok = await chatService.DeleteConversationAsync(UserId, conversationId, cancellationToken);
        return ok ? NoContent() : NotFound();
    }
}
