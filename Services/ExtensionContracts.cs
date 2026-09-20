using GameOrchestrator.Models;

namespace GameOrchestrator.Services;

public interface IScreenshotService
{
    Task<ScreenshotPayload?> CaptureForTaskAsync(RuntimeSession session, CancellationToken token = default);
    Task<ScreenshotPayload?> CaptureWindowAsync(IntPtr windowHandle, CancellationToken token = default);
    Task<ScreenshotPayload?> CaptureDesktopAsync(CancellationToken token = default);
}
public interface INotificationProvider { Task NotifyAsync(string eventName, RuntimeSession session, string? screenshotPath, CancellationToken token = default); }
public sealed class NullScreenshotService : IScreenshotService
{
    public Task<ScreenshotPayload?> CaptureForTaskAsync(RuntimeSession session, CancellationToken token = default) => Task.FromResult<ScreenshotPayload?>(null);
    public Task<ScreenshotPayload?> CaptureWindowAsync(IntPtr windowHandle, CancellationToken token = default) => Task.FromResult<ScreenshotPayload?>(null);
    public Task<ScreenshotPayload?> CaptureDesktopAsync(CancellationToken token = default) => Task.FromResult<ScreenshotPayload?>(null);
}
