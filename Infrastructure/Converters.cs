using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using GameOrchestrator.Models;

namespace GameOrchestrator.Infrastructure;

public sealed class OneBasedIndexConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is int index ? index + 1 : 1;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class StatusBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        TaskRunStatus.Running or TaskRunStatus.Starting or TaskRunStatus.CompletionDetected => new SolidColorBrush(Color.FromRgb(21, 112, 239)),
        TaskRunStatus.Completed => new SolidColorBrush(Color.FromRgb(34, 197, 94)),
        TaskRunStatus.Cleaning or TaskRunStatus.CleanupVerifying or TaskRunStatus.TimedOut => new SolidColorBrush(Color.FromRgb(245, 158, 11)),
        TaskRunStatus.Failed => new SolidColorBrush(Color.FromRgb(239, 68, 68)),
        _ => new SolidColorBrush(Color.FromRgb(148, 163, 184))
    };
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class LogLevelBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        LogLevel.Success => new SolidColorBrush(Color.FromRgb(22, 163, 74)),
        LogLevel.Warning => new SolidColorBrush(Color.FromRgb(217, 119, 6)),
        LogLevel.Error => new SolidColorBrush(Color.FromRgb(220, 38, 38)),
        _ => new SolidColorBrush(Color.FromRgb(37, 99, 235))
    };
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
