using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using GameOrchestrator.Models;
using GameOrchestrator.Services;
using GameOrchestrator.Views;

internal static class TimeSelectionTests
{
    public static void Run()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { CheckBindingsAndSchedule(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(15))) throw new TimeoutException("时间选择控件检查超时");
        if (failure is not null) throw new InvalidOperationException("时间选择检查失败", failure);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void CheckBindingsAndSchedule()
    {
        Check(new AutomationTaskConfig().MaxRunMinutes == 45, "新建自定义任务上限应为45分钟");
        var profiles = new KnownToolProfileService();
        foreach (var type in new[] { "BGI", "MAA", "ZOG", "MFA", "M7A" })
            Check(profiles.CreateProfile(type).MaxRunMinutes == 45, $"{type} 类型默认上限应为45分钟");

        var live = new AutomationTaskConfig { ScheduledStartTime = "09:30" };
        var draft = JsonSerializer.Deserialize<AutomationTaskConfig>(JsonSerializer.Serialize(live))!;
        var picker = new TaskTimeSelector();
        BindingOperations.SetBinding(picker, TaskTimeSelector.TimeProperty, new Binding(nameof(AutomationTaskConfig.ScheduledStartTime))
        {
            Source = draft, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
        });
        var hours = (ComboBox)picker.FindName("HourSelector");
        var minutes = (ComboBox)picker.FindName("MinuteSelector");
        Check(!hours.IsEditable && !minutes.IsEditable, "启动时间应仅允许下拉选择");
        Check(hours.SelectedItem?.ToString() == "09" && minutes.SelectedItem?.ToString() == "30", "已保存时间应显示正确");
        hours.SelectedItem = "23";
        minutes.SelectedItem = "59";
        Check(draft.ScheduledStartTime == "23:59" && live.ScheduledStartTime == "09:30", "选择应即时更新草稿而不修改原任务");
        Check(BindingOperations.IsDataBound(picker, TaskTimeSelector.TimeProperty), "选择操作不能移除双向绑定");
        hours.SelectedItem = "不定时";
        Check(draft.ScheduledStartTime == "" && !minutes.IsEnabled, "不定时应清空时间且禁用分钟");
        hours.SelectedItem = "00";
        Check(draft.ScheduledStartTime == "00:00" && minutes.IsEnabled, "零点应是有效启动时间");
        var groupDraft = new AutomationTaskConfig { IsGroup = true, ScheduledStartTime = "07:05" };
        BindingOperations.SetBinding(picker, TaskTimeSelector.TimeProperty, new Binding(nameof(AutomationTaskConfig.ScheduledStartTime))
        {
            Source = groupDraft, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
        });
        Check(hours.SelectedItem?.ToString() == "07" && minutes.SelectedItem?.ToString() == "05", "切换编辑对象后应刷新下拉框");
        minutes.SelectedItem = "15";
        var savedGroup = JsonSerializer.Deserialize<AutomationTaskConfig>(JsonSerializer.Serialize(groupDraft))!;
        Check(savedGroup.ScheduledStartTime == "07:15" && draft.ScheduledStartTime == "00:00", "组时间应正确持久化且不串改其他任务");
        picker.Measure(new Size(200, 40));
        picker.Arrange(new Rect(0, 0, 200, 40));
        Check(picker.DesiredSize.Width <= 200, "时间选择器应适应任务编辑区域");

        // Global preparation applies to both independent tasks and task-group anchors.
        var scheduledAt = DateTime.Now.AddSeconds(60);
        var time = scheduledAt.ToString("HH:mm:ss");
        var scheduledTask = new AutomationTaskConfig { ScheduledStartTime = time };
        var scheduledGroup = new AutomationTaskConfig { IsGroup = true, ScheduledStartTime = time };
        ScheduledLaunchBatch? batch = null;
        using var scheduler = new SchedulerService();
        scheduler.Start(() => new[] { scheduledTask, scheduledGroup }, () => 90, value =>
        {
            batch = value;
            return Task.CompletedTask;
        });
        Check(batch is not null && batch.Anchors.Count == 2 && (batch.ScheduledAt - batch.PrepareAt).TotalSeconds == 90,
            "独立任务和任务组应统一使用全局提前恢复时间");
    }
}
