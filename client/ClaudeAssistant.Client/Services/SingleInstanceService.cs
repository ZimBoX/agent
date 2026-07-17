using System.IO.Pipes;
using System.Text;

namespace ClaudeAssistant.Client.Services;

// Раньше каждый клик по пункту меню в Проводнике запускал новый процесс
// (и новый значок в трее), даже если клиент уже был открыт — некрасиво и
// сбивает с толку. Теперь: именованный Mutex решает, кто "главный" процесс;
// если клиент уже запущен, новый процесс просто передаёт пути файлов главному
// через именованный канал и сразу завершается, не создавая окно/трей вообще.
public class SingleInstanceService : IDisposable
{
    private const string MutexName = "ClaudeAssistant.Client.SingleInstance";
    private const string PipeName = "ClaudeAssistant.Client.Ipc";

    private readonly Mutex _mutex;
    private CancellationTokenSource? _listenCts;

    public bool IsPrimaryInstance { get; }

    public event Action<IReadOnlyList<string>>? FilesReceived;

    public SingleInstanceService()
    {
        _mutex = new Mutex(true, MutexName, out var createdNew);
        IsPrimaryInstance = createdNew;
    }

    // Вызывается на вторичном процессе — единственное, что он успевает сделать,
    // прежде чем сразу завершиться (см. App() — до InitializeComponent()). Вызывающий
    // код блокирует поток синхронно (.GetAwaiter().GetResult()) ещё до старта
    // message loop — ConfigureAwait(false) обязателен на каждом await здесь, иначе
    // продолжение попытается вернуться на ещё не крутящийся UI-контекст и получится
    // classic sync-over-async deadlock (ровно это и происходило до фикса).
    public static async Task ForwardToPrimaryAsync(IReadOnlyList<string> filePaths)
    {
        try
        {
            await using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.Asynchronous);
            await client.ConnectAsync(2000).ConfigureAwait(false);

            await using var writer = new StreamWriter(client, Encoding.UTF8) { AutoFlush = true };
            foreach (var path in filePaths)
            {
                await writer.WriteLineAsync(path).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // Главный процесс не отвечает (завис или как раз закрывается) — тихо
            // сдаёмся, вторичный процесс всё равно должен завершиться сразу же.
        }
    }

    // Вызывается на главном процессе — слушает канал в фоне всё время жизни
    // приложения, на каждое сообщение поднимает FilesReceived.
    public void StartListening()
    {
        _listenCts = new CancellationTokenSource();
        _ = ListenLoopAsync(_listenCts.Token);
    }

    private async Task ListenLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous
                );
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

                using var reader = new StreamReader(server, Encoding.UTF8);
                var paths = new List<string>();
                string? line;
                while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) is not null)
                {
                    paths.Add(line);
                }

                FilesReceived?.Invoke(paths);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                // Один сбойный коннект не должен насовсем убить цикл прослушивания.
            }
        }
    }

    public void Dispose()
    {
        _listenCts?.Cancel();
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
