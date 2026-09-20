using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using GameOrchestrator.Events;
using GameOrchestrator.Models;

namespace GameOrchestrator.Services;

public sealed record NotificationTestResult(bool Success, string Message);

public sealed class WeComNotificationService : IAsyncDisposable
{
    private sealed record OutboundMessage(string? Text, ScreenshotPayload? Image);
    private sealed record NotificationRequest(string WebhookUrl, string Description, string TaskName, IReadOnlyList<OutboundMessage> Messages);
    private const int MaximumTextBytes = 2048;
    private readonly Func<NotificationConfig> _configProvider;
    private readonly LoggingService _log;
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly Channel<NotificationRequest> _queue = Channel.CreateBounded<NotificationRequest>(
        new BoundedChannelOptions(32) { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
    private readonly Task _worker;

    public WeComNotificationService(TaskEventBus events, Func<NotificationConfig> configProvider, LoggingService log)
    {
        _configProvider = configProvider;
        _log = log;
        events.Subscribe<TaskStartedEvent>(message => QueueEvent("应用启动", message.Session, null, config => config.NotifyOnStart));
        events.Subscribe<TaskCompletedEvent>(message => QueueEvent("任务完成", message.Session, null, config => config.NotifyOnComplete));
        events.Subscribe<TaskFailedEvent>(message => QueueEvent("任务故障", message.Session, message.Error, config => config.NotifyOnFailure));
        events.Subscribe<TaskTimedOutEvent>(message => QueueEvent("强制终止", message.Session, $"运行超时：{message.Session.ExitReason}", config => config.NotifyOnForcedStop || config.NotifyOnTimeout));
        events.Subscribe<TaskStoppedEvent>(message => QueueEvent("强制终止", message.Session, $"用户终止：{message.Session.ExitReason}", config => config.NotifyOnForcedStop));
        _worker = ProcessQueueAsync();
    }

    public static bool TryValidateWebhook(string? value, out Uri? webhook, out string error)
    {
        webhook = null;
        error = "";
        if (string.IsNullOrWhiteSpace(value))
        {
            error = "请先填写企业微信群机器人 Webhook 地址。";
            return false;
        }
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out webhook)
            || webhook.Scheme != Uri.UriSchemeHttps
            || !string.Equals(webhook.Host, "qyapi.weixin.qq.com", StringComparison.OrdinalIgnoreCase)
            || !webhook.AbsolutePath.Equals("/cgi-bin/webhook/send", StringComparison.OrdinalIgnoreCase)
            || !webhook.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Any(part => part.StartsWith("key=", StringComparison.OrdinalIgnoreCase) && part.Length > 4))
        {
            webhook = null;
            error = "Webhook 地址无效，应为企业微信 qyapi.weixin.qq.com 的 HTTPS 群机器人地址。";
            return false;
        }
        return true;
    }

    public async Task<NotificationTestResult> SendTestAsync(CancellationToken token = default)
    {
        var config = _configProvider();
        if (!TryValidateWebhook(config.WeComWebhookUrl, out var webhook, out var error)) return new(false, error);
        try
        {
            await SendTextAsync(webhook!, $"[GameDayWork]\n企业微信通知测试成功\n时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}", token);
            await _log.WriteAsync(LogLevel.Success, "企业微信测试通知发送成功");
            return new(true, "测试消息已发送，请检查企业微信群。\n\nWebhook 密钥不会写入运行日志。");
        }
        catch (Exception ex)
        {
            await _log.WriteAsync(LogLevel.Error, $"企业微信测试通知发送失败：{ex.Message}");
            return new(false, $"发送失败：{ex.Message}");
        }
    }

    public void QueueRunningScreenshot(RuntimeSession session, IReadOnlyList<TrackedProcess> processes, ScreenshotPayload? image, string? error)
    {
        var lines = CreateHeader("任务运行截图", session, DateTimeOffset.Now);
        lines.Add("");
        AppendTrackedProcesses(lines, processes);
        lines.Add("");
        lines.Add(image is null ? $"运行截图：失败（{NormalizeError(error)}）" : "运行截图：已发送");
        QueueScreenshotBatch("任务运行截图", session, lines, image);
    }

    public void QueueEndScreenshot(RuntimeSession session, ScreenshotPayload? image, string? error)
    {
        var lines = CreateHeader("任务结束截图", session, DateTimeOffset.Now);
        lines.Add(image is null ? $"任务结束截图：失败（{NormalizeError(error)}）" : "任务结束截图：已发送");
        QueueScreenshotBatch("任务结束截图", session, lines, image);
    }

    private void QueueEvent(string eventName, RuntimeSession session, string? detail, Func<NotificationConfig, bool> eventEnabled)
    {
        var config = _configProvider();
        if (!config.Enabled || !eventEnabled(config) || string.IsNullOrWhiteSpace(config.WeComWebhookUrl)) return;
        var text = BuildEventMessage(eventName, session, detail, DateTimeOffset.Now);
        TryQueue(new(config.WeComWebhookUrl.Trim(), eventName, session.TaskName, [new(text, null)]));
    }

    private void QueueScreenshotBatch(string description, RuntimeSession session, List<string> lines, ScreenshotPayload? image)
    {
        var config = _configProvider();
        if (!config.Enabled || !config.CaptureTaskScreenshots || string.IsNullOrWhiteSpace(config.WeComWebhookUrl)) return;
        var messages = new List<OutboundMessage> { new(LimitUtf8(string.Join("\n", lines)), null) };
        if (image is not null) messages.Add(new(null, image));
        TryQueue(new(config.WeComWebhookUrl.Trim(), description, session.TaskName, messages));
    }

    private void TryQueue(NotificationRequest request)
    {
        if (_queue.Writer.TryWrite(request)) return;
        _ = _log.WriteAsync(LogLevel.Error, $"企业微信通知队列已满，已丢弃：{request.Description} / {request.TaskName}");
    }

    private async Task ProcessQueueAsync()
    {
        await foreach (var request in _queue.Reader.ReadAllAsync())
        {
            try
            {
                if (!TryValidateWebhook(request.WebhookUrl, out var webhook, out var error)) throw new InvalidOperationException(error);
                foreach (var message in request.Messages)
                {
                    if (message.Text is not null) await SendWithRetryAsync(() => SendTextAsync(webhook!, message.Text, CancellationToken.None));
                    if (message.Image is not null) await SendWithRetryAsync(() => SendImageAsync(webhook!, message.Image, CancellationToken.None));
                }
                await _log.WriteAsync(LogLevel.Success, $"企业微信通知已发送：{request.Description} / {request.TaskName}");
            }
            catch (Exception ex)
            {
                await _log.WriteAsync(LogLevel.Error, $"企业微信通知发送失败（{request.Description} / {request.TaskName}）：{ex.Message}");
            }
        }
    }

    private async Task SendWithRetryAsync(Func<Task> send)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { await send(); return; }
            catch (Exception ex) when (attempt < 3 && IsTransient(ex))
            {
                await _log.WriteAsync(LogLevel.Warning, $"企业微信发送暂时失败，准备第 {attempt + 1} 次尝试：{ex.Message}");
                await Task.Delay(TimeSpan.FromMilliseconds(400 * attempt));
            }
        }
    }

    private static bool IsTransient(Exception exception) => exception is HttpRequestException or TaskCanceledException
        || exception is WeComHttpException { StatusCode: HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests }
        || exception is WeComHttpException { StatusCode: >= HttpStatusCode.InternalServerError };

    private static string BuildEventMessage(string eventName, RuntimeSession session, string? detail, DateTimeOffset eventTime)
    {
        var lines = CreateHeader(eventName, session, eventTime);
        var duration = (session.EndTime ?? eventTime) - session.StartTime;
        if (session.EndTime is not null) lines.Add($"耗时：{duration:hh\\:mm\\:ss}");
        var description = string.IsNullOrWhiteSpace(detail) ? session.ExitReason : detail;
        if (!string.IsNullOrWhiteSpace(description)) lines.Add($"说明：{description}");
        AppendTerminatedProcesses(lines, session.SnapshotTerminatedProcesses(),
            eventName == "强制终止" ? "强制终止进程" : "终结进程");
        return LimitUtf8(string.Join("\n", lines));
    }

    private static List<string> CreateHeader(string eventName, RuntimeSession session, DateTimeOffset eventTime)
    {
        var lines = new List<string>
        {
            "[GameDayWork]", $"事件：{eventName}", $"任务：{session.TaskName}",
            $"时间：{eventTime:yyyy-MM-dd HH:mm:ss zzz}", $"状态：{session.Status}"
        };
        if (session.RootPid > 0) lines.Add($"PID：{session.RootPid}");
        return lines;
    }

    private static void AppendTrackedProcesses(List<string> lines, IReadOnlyList<TrackedProcess> processes)
    {
        var unique = processes.GroupBy(process => process.Pid).Select(group => group.First()).OrderBy(process => process.Pid).ToList();
        lines.Add($"正在监控的进程：{unique.Count} 个");
        foreach (var process in unique.Take(12)) lines.Add($"- {DisplayProcessName(process.ProcessName)}（PID {process.Pid}，{process.Source}）");
        if (unique.Count > 12) lines.Add($"- 其余 {unique.Count - 12} 个详见本地日志");
    }

    private static void AppendTerminatedProcesses(List<string> lines, IReadOnlyList<ProcessTerminationRecord> processes, string heading)
    {
        if (processes.Count == 0) return;
        lines.Add("");
        lines.Add($"{heading}：{processes.Count} 个");
        foreach (var process in processes.Take(10))
        {
            var result = process.Success ? "成功" : $"失败：{NormalizeError(process.Error)}";
            lines.Add($"- {DisplayProcessName(process.ProcessName)}（PID {process.Pid}，{process.Source}，{process.Method}，{result}）");
        }
        if (processes.Count > 10) lines.Add($"- 其余 {processes.Count - 10} 个详见本地日志");
    }

    private static string DisplayProcessName(string name) => name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : name + ".exe";

    private static string NormalizeError(string? error)
    {
        if (string.IsNullOrWhiteSpace(error)) return "未知错误";
        var normalized = error.Replace('\r', ' ').Replace('\n', ' ');
        return normalized[..Math.Min(normalized.Length, 300)];
    }

    private static string LimitUtf8(string value)
    {
        if (Encoding.UTF8.GetByteCount(value) <= MaximumTextBytes) return value;
        const string suffix = "\n内容过长，其余详见本地日志";
        var limit = MaximumTextBytes - Encoding.UTF8.GetByteCount(suffix);
        var builder = new StringBuilder();
        var bytes = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            var text = rune.ToString();
            var next = Encoding.UTF8.GetByteCount(text);
            if (bytes + next > limit) break;
            builder.Append(text);
            bytes += next;
        }
        return builder + suffix;
    }

    private async Task SendTextAsync(Uri webhook, string content, CancellationToken token) =>
        await SendPayloadAsync(webhook, new { msgtype = "text", text = new { content } }, token);

    private async Task SendImageAsync(Uri webhook, ScreenshotPayload image, CancellationToken token)
    {
        if (image.Data.Length > 2_000_000) throw new InvalidOperationException("图片超过企业微信 2 MB 限制");
        var md5 = Convert.ToHexString(MD5.HashData(image.Data)).ToLowerInvariant();
        await SendPayloadAsync(webhook, new
        {
            msgtype = "image",
            image = new { base64 = Convert.ToBase64String(image.Data), md5 }
        }, token);
    }

    private async Task SendPayloadAsync(Uri webhook, object payload, CancellationToken token)
    {
        using var response = await _httpClient.PostAsJsonAsync(webhook, payload, token);
        var responseText = await response.Content.ReadAsStringAsync(token);
        if (!response.IsSuccessStatusCode) throw new WeComHttpException(response.StatusCode, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
        using var document = JsonDocument.Parse(responseText);
        var root = document.RootElement;
        var errorCode = root.TryGetProperty("errcode", out var code) ? code.GetInt32() : -1;
        var errorMessage = root.TryGetProperty("errmsg", out var message) ? message.GetString() : "未知响应";
        if (errorCode != 0) throw new InvalidOperationException($"企业微信返回 {errorCode}：{errorMessage}");
    }

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        await _worker;
        _httpClient.Dispose();
    }

    private sealed class WeComHttpException(HttpStatusCode statusCode, string message) : Exception(message)
    {
        public HttpStatusCode StatusCode { get; } = statusCode;
    }
}
