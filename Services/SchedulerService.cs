using System.Windows.Threading;
using GameOrchestrator.Models;

namespace GameOrchestrator.Services;

public sealed class SchedulerService : IDisposable
{
    private readonly DispatcherTimer _timer;
    private DateTime? _lastRunDate;
    private Func<Task>? _callback;
    private ScheduleConfig? _config;
    private bool _tickRunning;

    public event Action<Exception>? Error;

    public SchedulerService()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(20)
        };
        _timer.Tick += Timer_Tick;
    }

    public void Start(ScheduleConfig config, Func<Task> callback)
    {
        _config = config;
        _callback = callback;
        _timer.Start();
        _ = TickSafelyAsync();
    }

    public DateTime? NextRun
    {
        get
        {
            if (_config?.Enabled != true || !IsValidTime(_config.Time)) return null;
            var now = DateTime.Now;
            for (var offset = 0; offset < 8; offset++)
            {
                var day = now.Date.AddDays(offset);
                var candidate = day + _config.Time;
                if (candidate > now && IsAllowed(day.DayOfWeek)) return candidate;
            }
            return null;
        }
    }

    private async void Timer_Tick(object? sender, EventArgs e) => await TickSafelyAsync();

    private async Task TickSafelyAsync()
    {
        if (_tickRunning) return;
        _tickRunning = true;
        try
        {
            await TickAsync();
        }
        catch (Exception ex)
        {
            Error?.Invoke(ex);
        }
        finally
        {
            _tickRunning = false;
        }
    }

    private async Task TickAsync()
    {
        if (_config?.Enabled != true || _callback is null || !IsValidTime(_config.Time) || !IsAllowed(DateTime.Today.DayOfWeek)) return;
        var now = DateTime.Now;
        var delay = now.TimeOfDay - _config.Time;
        if (delay < TimeSpan.Zero || delay >= TimeSpan.FromSeconds(40) || _lastRunDate == now.Date) return;
        _lastRunDate = now.Date;
        await _callback();
    }

    private static bool IsValidTime(TimeSpan time) => time >= TimeSpan.Zero && time < TimeSpan.FromDays(1);

    private bool IsAllowed(DayOfWeek day) => _config!.Repeat switch
    {
        ScheduleRepeat.Daily => true,
        ScheduleRepeat.Weekdays => day is >= DayOfWeek.Monday and <= DayOfWeek.Friday,
        _ => _config.SelectedDays.Contains(day)
    };

    public void Dispose()
    {
        _timer.Stop();
        _timer.Tick -= Timer_Tick;
    }
}
