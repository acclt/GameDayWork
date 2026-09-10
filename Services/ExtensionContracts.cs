using GameOrchestrator.Models;

namespace GameOrchestrator.Services;

public interface IScreenshotService
{
    Task<string?> CaptureForTaskAsync(RuntimeSession session, CancellationToken token = default);
    Task<string?> CaptureWindowAsync(IntPtr windowHandle, CancellationToken token = default);
    Task<string?> CaptureDesktopAsync(CancellationToken token = default);
}
public interface INotificationProvider { Task NotifyAsync(string eventName, RuntimeSession session, string? screenshotPath, CancellationToken token = default); }
public sealed class NullScreenshotService : IScreenshotService
{
    public Task<string?> CaptureForTaskAsync(RuntimeSession session, CancellationToken token = default) => Task.FromResult<string?>(null);
    public Task<string?> CaptureWindowAsync(IntPtr windowHandle, CancellationToken token = default) => Task.FromResult<string?>(null);
    public Task<string?> CaptureDesktopAsync(CancellationToken token = default) => Task.FromResult<string?>(null);
}
