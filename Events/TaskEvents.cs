using GameOrchestrator.Models;

namespace GameOrchestrator.Events;

public interface IDomainEvent { DateTimeOffset OccurredAt { get; } }
public abstract record TaskEvent(RuntimeSession Session) : IDomainEvent { public DateTimeOffset OccurredAt { get; } = DateTimeOffset.Now; }
public sealed record TaskStartingEvent(RuntimeSession Session) : TaskEvent(Session);
public sealed record TaskStartedEvent(RuntimeSession Session) : TaskEvent(Session);
public sealed record TaskCompletionDetectedEvent(RuntimeSession Session) : TaskEvent(Session);
public sealed record TaskCleanupStartedEvent(RuntimeSession Session) : TaskEvent(Session);
public sealed record TaskCleanupCompletedEvent(RuntimeSession Session) : TaskEvent(Session);
public sealed record TaskCompletedEvent(RuntimeSession Session) : TaskEvent(Session);
public sealed record TaskFailedEvent(RuntimeSession Session, string Error) : TaskEvent(Session);
public sealed record TaskTimedOutEvent(RuntimeSession Session) : TaskEvent(Session);
public sealed record TaskStoppedEvent(RuntimeSession Session) : TaskEvent(Session);
public sealed record QueueStartedEvent : IDomainEvent { public DateTimeOffset OccurredAt { get; } = DateTimeOffset.Now; }
public sealed record QueueCompletedEvent : IDomainEvent { public DateTimeOffset OccurredAt { get; } = DateTimeOffset.Now; }
public sealed record QueueFailedEvent(string Error) : IDomainEvent { public DateTimeOffset OccurredAt { get; } = DateTimeOffset.Now; }

public sealed class TaskEventBus
{
    private readonly object _gate = new();
    private readonly Dictionary<Type, List<Delegate>> _handlers = [];
    public void Subscribe<T>(Action<T> handler) where T : IDomainEvent
    { lock (_gate) { if (!_handlers.TryGetValue(typeof(T), out var list)) _handlers[typeof(T)] = list = []; list.Add(handler); } }
    public void Publish<T>(T message) where T : IDomainEvent
    {
        Delegate[] handlers;
        lock (_gate) handlers = _handlers.TryGetValue(typeof(T), out var list) ? [.. list] : [];
        foreach (var handler in handlers) ((Action<T>)handler)(message);
    }
}
