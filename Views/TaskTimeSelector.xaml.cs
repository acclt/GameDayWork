using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using GameOrchestrator.Services;

namespace GameOrchestrator.Views;

public partial class TaskTimeSelector : UserControl
{
    public static readonly DependencyProperty TimeProperty = DependencyProperty.Register(
        nameof(Time), typeof(string), typeof(TaskTimeSelector),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (control, _) => ((TaskTimeSelector)control).RefreshSelection()));

    private bool _refreshing;

    public string Time
    {
        get => (string)GetValue(TimeProperty);
        set => SetValue(TimeProperty, value);
    }

    public TaskTimeSelector()
    {
        InitializeComponent();
        _refreshing = true;
        HourSelector.ItemsSource = new[] { "不定时" }.Concat(
            Enumerable.Range(0, 24).Select(hour => hour.ToString("00", CultureInfo.InvariantCulture))).ToArray();
        MinuteSelector.ItemsSource = Enumerable.Range(0, 60)
            .Select(minute => minute.ToString("00", CultureInfo.InvariantCulture)).ToArray();
        _refreshing = false;
        RefreshSelection();
    }

    private void RefreshSelection()
    {
        if (HourSelector is null || MinuteSelector is null) return;
        _refreshing = true;
        try
        {
            var scheduled = SchedulerService.TryParseTime(Time, out var time);
            HourSelector.SelectedIndex = scheduled ? time.Hours + 1 : 0;
            MinuteSelector.SelectedIndex = scheduled ? time.Minutes : 0;
            MinuteSelector.IsEnabled = scheduled;
        }
        finally { _refreshing = false; }
    }

    private void Time_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshing || HourSelector is null || MinuteSelector is null) return;
        var scheduled = HourSelector.SelectedIndex > 0;
        MinuteSelector.IsEnabled = scheduled;
        // SetCurrentValue preserves the editor's two-way binding when the user selects a time.
        SetCurrentValue(TimeProperty, scheduled
            ? $"{HourSelector.SelectedIndex - 1:00}:{Math.Max(0, MinuteSelector.SelectedIndex):00}"
            : "");
    }
}
