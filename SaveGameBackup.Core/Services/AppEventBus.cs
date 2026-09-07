using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace SaveGameBackup.Core.Services;

/// <summary>
/// Triển khai Mediator Pattern (Event Aggregator) để truyền nhận thông điệp giữa các components
/// một cách lỏng lẻo (loose coupling), không gây phụ thuộc vòng.
/// </summary>
public interface IAppEventBus
{
    void Subscribe<TMessage>(Action<TMessage> handler);
    void Unsubscribe<TMessage>(Action<TMessage> handler);
    void Publish<TMessage>(TMessage message);
}

public class AppEventBus : IAppEventBus
{
    private readonly ConcurrentDictionary<Type, List<Delegate>> _subscriptions = new();
    private readonly object _lock = new();

    public void Subscribe<TMessage>(Action<TMessage> handler)
    {
        var msgType = typeof(TMessage);
        lock (_lock)
        {
            var list = _subscriptions.GetOrAdd(msgType, _ => new List<Delegate>());
            if (!list.Contains(handler))
            {
                list.Add(handler);
            }
        }
    }

    public void Unsubscribe<TMessage>(Action<TMessage> handler)
    {
        var msgType = typeof(TMessage);
        lock (_lock)
        {
            if (_subscriptions.TryGetValue(msgType, out var list))
            {
                list.Remove(handler);
            }
        }
    }

    public void Publish<TMessage>(TMessage message)
    {
        var msgType = typeof(TMessage);
        List<Delegate>? handlersCopy = null;

        lock (_lock)
        {
            if (_subscriptions.TryGetValue(msgType, out var list))
            {
                handlersCopy = new List<Delegate>(list);
            }
        }

        if (handlersCopy != null)
        {
            foreach (var del in handlersCopy)
            {
                if (del is Action<TMessage> action)
                {
                    try
                    {
                        action(message);
                    }
                    catch { }
                }
            }
        }
    }
}

// Các Events trao đổi giữa các components
public record BackupCompletedEvent(string GameName, string? BackupPath, int FileCount = 0, long TotalSize = 0);
public record HistoryChangedEvent();
public record GameSelectedForBackupEvent(string GameName);
public record RequestNavigateTabEvent(int TabIndex);
