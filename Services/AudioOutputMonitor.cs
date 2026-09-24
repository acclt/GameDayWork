using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GameOrchestrator.Models;
using NAudio.CoreAudioApi;

namespace GameOrchestrator.Services;

/// <summary>Tracks real signal on active Windows playback endpoints.</summary>
internal sealed class AudioOutputMonitor : IAsyncDisposable
{
    private static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan DeviceRefreshInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SoundGracePeriod = TimeSpan.FromSeconds(2);
    private const float MinimumPeak = 0.001f;

    private readonly LoggingService _log;
    private readonly CancellationTokenSource _stop = new();
    private Task? _monitorTask;
    private long _lastSoundTick = long.MinValue;

    public AudioOutputMonitor(LoggingService log) => _log = log;

    public bool HasRecentOutput
    {
        get
        {
            var last = Interlocked.Read(ref _lastSoundTick);
            return last != long.MinValue && Environment.TickCount64 - last <= SoundGracePeriod.TotalMilliseconds;
        }
    }

    public void Start() => _monitorTask ??= Task.Run(() => MonitorAsync(_stop.Token));

    private async Task MonitorAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var devices = new List<MMDevice>();
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                using var timer = new PeriodicTimer(SampleInterval);
                var refreshedAt = long.MinValue;

                while (!token.IsCancellationRequested)
                {
                    var now = Environment.TickCount64;
                    if (refreshedAt == long.MinValue || now - refreshedAt >= DeviceRefreshInterval.TotalMilliseconds)
                    {
                        DisposeDevices(devices);
                        devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
                        refreshedAt = now;
                    }

                    foreach (var device in devices)
                    {
                        var volume = device.AudioEndpointVolume;
                        if (!volume.Mute && volume.MasterVolumeLevelScalar > 0f
                            && device.AudioMeterInformation.MasterPeakValue > MinimumPeak)
                        {
                            Interlocked.Exchange(ref _lastSoundTick, now);
                            break;
                        }
                    }

                    if (!await timer.WaitForNextTickAsync(token)) break;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                await _log.WriteAsync(LogLevel.Warning, $"音频输出检测失败，稍后重试：{ex.Message}");
                try { await Task.Delay(DeviceRefreshInterval, token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            }
            finally
            {
                DisposeDevices(devices);
            }
        }
    }

    private static void DisposeDevices(List<MMDevice> devices)
    {
        foreach (var device in devices) device.Dispose();
        devices.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if (_monitorTask is not null) await _monitorTask;
        _stop.Dispose();
    }
}
