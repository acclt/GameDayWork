using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using GameOrchestrator.Events;
using GameOrchestrator.Models;

namespace GameOrchestrator.Services;

public sealed record NotificationTestResult(bool Success, string Message);

public sealed class WeComNotificationService : IAsyncDisposable
{
    private sealed record NotificationRequest(
        string WebhookUrl,
        string EventName,
        string TaskName,
        TaskRunStatus Status,
        int RootPid,
        DateTimeOffset EventTime,
        DateTimeOffset StartTime,
        DateTimeOffset? EndTime,
        string ExitReason,
        string? Detail);

    private readonly Func<NotificationConfig> _configProvider;
    private readonly LoggingService _log;
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly Channel<NotificationRequest> _queue = Channel.CreateUnbounded<NotificationRequest>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly Task _worker;

    public WeComNotificationService(TaskEventBus events, Func<NotificationConfig> configProvider, LoggingService log)
    {
        _configProvider = configProvider;
        _log = log;
        events.Subscribe<TaskStartedEvent>(message => Queue("应用启动", message.Session, null, config => config.NotifyOnStart));
        events.Subscribe<TaskCompletedEvent>(message => Queue("任务完成", message.Session, null, config => config.NotifyOnComplete));
        events.Subscribe<TaskFailedEvent>(message => Queue("任务故障", message.Session, message.Error, config => config.NotifyOnFailure));
        events.Subscribe<TaskTimedOutEvent>(message => Queue("强制终止", message.Session, $"运行超时：{message.Session.ExitReason}", config => config.NotifyOnForcedStop || config.NotifyOnTimeout));
        events.Subscribe<TaskStoppedEvent>(message => Queue("强制终止", message.Session, $"用户终止：{message.Session.ExitReason}", config => config.NotifyOnForcedStop));
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
        if (!TryValidateWebhook(config.WeComWebhookUrl, out var webhook, out var error))
            return new(false, error);

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

    private void Queue(string eventName, RuntimeSession session, string? detail, Func<NotificationConfig, bool> eventEnabled)
    {
        var config = _configProvider();
        if (!config.Enabled || !eventEnabled(config) || string.IsNullOrWhiteSpace(config.WeComWebhookUrl)) return;
        _queue.Writer.TryWrite(new(
            config.WeComWebhookUrl.Trim(),
            eventName,
            session.TaskName,
            session.Status,
            session.RootPid,
            DateTimeOffset.Now,
            session.StartTime,
            session.EndTime,
            session.ExitReason,
            detail));
    }

    private async Task ProcessQueueAsync()
    {
        await foreach (var request in _queue.Reader.ReadAllAsync())
        {
            try
            {
                if (!TryValidateWebhook(request.WebhookUrl, out var webhook, out var error))
                    throw new InvalidOperationException(error);
                await SendTextAsync(webhook!, BuildMessage(request), CancellationToken.None);
                await _log.WriteAsync(LogLevel.Success, $"企业微信通知已发送：{request.EventName} / {request.TaskName}");
            }
            catch (Exception ex)
            {
                await _log.WriteAsync(LogLevel.Error, $"企业微信通知发送失败（{request.EventName} / {request.TaskName}）：{ex.Message}");
            }
        }
    }

    private static string BuildMessage(NotificationRequest request)
    {
        var duration = (request.EndTime ?? request.EventTime) - request.StartTime;
        var lines = new List<string>
        {
            "[GameDayWork]",
            $"事件：{request.EventName}",
            $"任务：{request.TaskName}",
            $"时间：{request.EventTime:yyyy-MM-dd HH:mm:ss zzz}",
            $"状态：{request.Status}"
        };
        if (request.RootPid > 0) lines.Add($"PID：{request.RootPid}");
        if (request.EndTime is not null) lines.Add($"耗时：{duration:hh\\:mm\\:ss}");
        var detail = string.IsNullOrWhiteSpace(request.Detail) ? request.ExitReason : request.Detail;
        if (!string.IsNullOrWhiteSpace(detail)) lines.Add($"说明：{detail[..Math.Min(detail.Length, 500)]}");
        return string.Join("\n", lines);
    }

    private async Task SendTextAsync(Uri webhook, string content, CancellationToken token)
    {
        using var response = await _httpClient.PostAsJsonAsync(webhook, new
        {
            msgtype = "text",
            text = new { content }
        }, token);
        var responseText = await response.Content.ReadAsStringAsync(token);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");

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
}
