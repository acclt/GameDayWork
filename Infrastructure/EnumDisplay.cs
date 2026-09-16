using System.Globalization;
using System.Windows.Data;
using GameOrchestrator.Models;

namespace GameOrchestrator.Infrastructure;

public sealed class EnumDisplayConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        FailurePolicy.ForceCleanupAndContinue => "强制清理并继续下一项",
        FailurePolicy.RetryCurrentTask => "重试当前任务",
        FailurePolicy.SkipCurrentTask => "跳过当前任务",
        FailurePolicy.StopQueue => "停止整个队列",
        CompletionDetectionMode.MainProcessExit => "主程序退出（推荐）",
        CompletionDetectionMode.SpecifiedProcessExit => "指定进程退出",
        CompletionDetectionMode.LogKeyword => "日志关键字",
        CompletionDetectionMode.Custom => "自定义检测（预留）",
        ScheduleRepeat.Daily => "每天",
        ScheduleRepeat.Weekdays => "工作日",
        ScheduleRepeat.SelectedDays => "指定星期",
        TaskCompletionAction.RunNext => "立即运行下一项",
        TaskCompletionAction.None => "无操作",
        _ => value?.ToString() ?? ""
    };
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
