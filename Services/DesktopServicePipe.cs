using System.IO.Pipes;

namespace GameOrchestrator.Services;

public sealed class DesktopServicePipe : IAsyncDisposable
{
    private readonly int _sessionId;
    private readonly Func<string, Task> _handler;
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _serverTask;

    public DesktopServicePipe(int sessionId, Func<string, Task> handler)
    {
        _sessionId = sessionId;
        _handler = handler;
    }

    public void Start() => _serverTask ??= RunAsync();

    private async Task RunAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream($"GameDayWork.Desktop.{_sessionId}", PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await pipe.WaitForConnectionAsync(_shutdown.Token);
                using var reader = new StreamReader(pipe);
                var command = await reader.ReadLineAsync(_shutdown.Token);
                if (!string.IsNullOrWhiteSpace(command)) await _handler(command);
            }
            catch (OperationCanceledException) { break; }
            catch { await Task.Delay(500, _shutdown.Token); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        if (_serverTask is not null)
        {
            try { await _serverTask; } catch (OperationCanceledException) { }
        }
        _shutdown.Dispose();
    }
}
