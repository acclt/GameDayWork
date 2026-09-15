using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
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
    public ScheduleWindow(ScheduleConfig config)
    {
        Config = config; InitializeComponent(); DataContext = this;
#pragma warning disable CS0618
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding { Converter = new Infrastructure.EnumDisplayConverter() });
        foreach (var combo in FindVisualChildren<ComboBox>(this)) combo.ItemTemplate = new DataTemplate { VisualTree = text };
#pragma warning restore CS0618
    }
    private bool Has(DayOfWeek day) => Config.SelectedDays.Contains(day);
    private void Set(DayOfWeek day, bool selected) { if (selected) Config.SelectedDays.Add(day); else Config.SelectedDays.Remove(day); }
    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var descendant in FindVisualChildren<T>(child)) yield return descendant;
        }
    }
    private void Save_Click(object sender, RoutedEventArgs e) { DialogResult = true; Close(); }
}
