using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GameOrchestrator.Models;

namespace GameOrchestrator.Views;

public partial class ScheduleWindow : Window
{
    private readonly ScheduleConfig _target;

    public ScheduleConfig Config { get; }
    public Array RepeatValues => Enum.GetValues(typeof(ScheduleRepeat));
    public bool Enabled { get => Config.Enabled; set => Config.Enabled = value; }
    public bool Monday { get => Has(DayOfWeek.Monday); set => Set(DayOfWeek.Monday, value); }
    public bool Tuesday { get => Has(DayOfWeek.Tuesday); set => Set(DayOfWeek.Tuesday, value); }
    public bool Wednesday { get => Has(DayOfWeek.Wednesday); set => Set(DayOfWeek.Wednesday, value); }
    public bool Thursday { get => Has(DayOfWeek.Thursday); set => Set(DayOfWeek.Thursday, value); }
    public bool Friday { get => Has(DayOfWeek.Friday); set => Set(DayOfWeek.Friday, value); }
    public bool Saturday { get => Has(DayOfWeek.Saturday); set => Set(DayOfWeek.Saturday, value); }
    public bool Sunday { get => Has(DayOfWeek.Sunday); set => Set(DayOfWeek.Sunday, value); }

    public ScheduleWindow(ScheduleConfig config)
    {
        _target = config;
        Config = new ScheduleConfig
        {
            Enabled = config.Enabled,
            Repeat = config.Repeat,
            Time = config.Time,
            SelectedDays = [.. config.SelectedDays]
        };
        InitializeComponent();
        DataContext = this;
        TimeInput.Text = config.Time >= TimeSpan.Zero && config.Time < TimeSpan.FromDays(1)
            ? config.Time.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture)
            : "";
    }

    private bool Has(DayOfWeek day) => Config.SelectedDays.Contains(day);

    private void Set(DayOfWeek day, bool selected)
    {
        if (selected) Config.SelectedDays.Add(day);
        else Config.SelectedDays.Remove(day);
    }

    private void TimeInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (TimeError is null) return;
        TimeError.Visibility = Visibility.Collapsed;
        TimeInput.BorderBrush = (Brush)FindResource("BorderBrush");
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!DateTime.TryParseExact(TimeInput.Text.Trim(), "HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            TimeError.Visibility = Visibility.Visible;
            TimeInput.BorderBrush = Brushes.Red;
            TimeInput.Focus();
            TimeInput.SelectAll();
            return;
        }

        _target.Enabled = Config.Enabled;
        _target.Repeat = Config.Repeat;
        _target.Time = parsed.TimeOfDay;
        _target.SelectedDays = [.. Config.SelectedDays];
        DialogResult = true;
        Close();
    }
}
