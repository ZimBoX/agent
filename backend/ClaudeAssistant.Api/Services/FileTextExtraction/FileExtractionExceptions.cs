namespace ClaudeAssistant.Api.Services.FileTextExtraction;

public class UnsupportedFileFormatException(string fileName) : Exception($"Неподдерживаемый формат файла: {fileName}");

public class UnreadablePdfException(string fileName) : Exception($"Нечитаемый PDF (нет текстового слоя): {fileName}");

public class FileValidationException(string message) : Exception(message);
