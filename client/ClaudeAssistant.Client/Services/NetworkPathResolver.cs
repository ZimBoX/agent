using System.Runtime.InteropServices;
using System.Text;

namespace ClaudeAssistant.Client.Services;

// Преобразует путь на подключённом сетевом диске (например, "X:\папка\...") в
// UNC-форму ("\\server\share\папка\..."). Зачем: буквы сетевых дисков (X:, Y:)
// видны только в той logon-сессии пользователя, где их подключили. Процесс
// бэкенда обычно живёт в другой сессии/контексте и этих букв не видит —
// Directory.Exists("X:\...") у него возвращает false, и запрос падает с
// "Указанная папка недоступна", хотя сам файл клиент читает нормально (клиент —
// в сессии пользователя, диск видит). UNC-путь от подключённых букв и сессии не
// зависит: любой процесс, работающий под тем же пользователем, обращается к
// сетевой шаре по своим сетевым учётным данным. Поэтому папку-объект отправляем
// на бэкенд именно в UNC-форме.
public static class NetworkPathResolver
{
    private const int NoError = 0;

    // Если path — на подключённом сетевом диске, возвращает его UNC-форму; иначе
    // (локальный путь, уже UNC, или буква не смаплена) — как есть.
    public static string ToUncIfMapped(string path)
    {
        // Интересуют только пути вида "<буква>:\..." — у UNC и относительных
        // второй символ не ':'.
        if (string.IsNullOrEmpty(path) || path.Length < 2 || path[1] != ':')
        {
            return path;
        }

        var drive = path[..2]; // "X:"
        var buffer = new StringBuilder(1024);
        var length = buffer.Capacity;

        var result = WNetGetConnection(drive, buffer, ref length);
        if (result != NoError)
        {
            // Не сетевой диск (обычный локальный) или мэппинг недоступен — оставляем
            // путь как есть, пусть решает бэкенд.
            return path;
        }

        var uncRoot = buffer.ToString().TrimEnd('\\'); // "\\server\share"
        var rest = path[2..]; // "\папка\файл"
        return uncRoot + rest;
    }

    // remoteName получает UNC-имя для локального имени устройства (буквы диска).
    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetGetConnection(string localName, StringBuilder remoteName, ref int length);
}
