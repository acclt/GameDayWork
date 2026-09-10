using System.Windows;
using GameOrchestrator.Models;

namespace GameOrchestrator.Views;

public partial class ScheduleWindow : Window
{
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
    public ScheduleWindow(ScheduleConfig config) { Config = config; InitializeComponent(); DataContext = this; }
    private bool Has(DayOfWeek day) => Config.SelectedDays.Contains(day);
    private void Set(DayOfWeek day, bool selected) { if (selected) Config.SelectedDays.Add(day); else Config.SelectedDays.Remove(day); }
    private void Save_Click(object sender, RoutedEventArgs e) { DialogResult = true; Close(); }
}
