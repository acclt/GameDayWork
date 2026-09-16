namespace GameOrchestrator.Models;

public enum TaskRunStatus { Idle, Waiting, Starting, Running, CompletionDetected, Cleaning, CleanupVerifying, Completed, Failed, TimedOut, Skipped, Stopped }
public enum QueueRunStatus { Idle, Running, Stopping, Completed, Failed }
public enum QueueExecutionMode { Sequential, Single }
public enum CompletionDetectionMode { MainProcessExit, SpecifiedProcessExit, LogKeyword, Custom }
public enum FailurePolicy { ForceCleanupAndContinue, RetryCurrentTask, SkipCurrentTask, StopQueue }
public enum TrackedProcessSource { Root, Child, JobObject, RuleMatched, Manual }
public enum LogLevel { Info, Success, Warning, Error }
public enum ScheduleRepeat { Daily, Weekdays, SelectedDays }
public enum TaskCompletionAction { RunNext, None }
public enum ScreenManagerState { IdleMonitoring, Blackout, PreparingTask, RunningTask }
