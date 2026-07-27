namespace ClaudeAssistant.Api.Services.FileGeneration;

// Сгенерированный моделью файл (create_file), который несётся вверх по запросу до
// контроллера/ChatService — там он сохраняется в БД (GeneratedFileRecord) с
// привязкой к записи истории или сообщению чата. Id генерируется в момент
// создания и совпадает с тем, по которому клиент потом скачает файл
// (GET /api/files/{id}).
public record GeneratedFileData(Guid Id, string FileName, string ContentType, byte[] Bytes);
