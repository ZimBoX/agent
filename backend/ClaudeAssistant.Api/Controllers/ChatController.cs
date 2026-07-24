using System.Security.Claims;
using ClaudeAssistant.Api.Dtos;
using ClaudeAssistant.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeAssistant.Api.Controllers;

[ApiController]
[Route("api/chat")]
[Authorize]
public class ChatController(ChatService chatService) : ControllerBase
{
    private Guid UserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    // Отправить сообщение в чат. Ответ стримится по токенам в SignalR
    // (ChatToken), а тут возвращается целиком по завершении — авторитетный текст
    // + conversationId/title для привязки нового диалога на клиенте.
    [HttpPost("send")]
    public async Task<ActionResult<ChatSendResponse>> Send([FromBody] ChatSendRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Text))
        {
            return BadRequest(new { error = "Пустое сообщение" });
        }

        var response = await chatService.SendAsync(UserId, request.ConversationId, request.Text.Trim(), cancellationToken);
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
}
