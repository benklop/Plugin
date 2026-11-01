using System;
using System.Collections.Generic;

namespace Moonfin.Households;

/// <summary>
/// Counts actions per user in a sliding window and turns the excess away, so a manager's account
/// can't be used to mint sessions or approve codes in bulk.
/// </summary>
public sealed class ActionRateLimiter
{
    private readonly int _limit;
    private readonly TimeSpan _window;
    private readonly Func<DateTime> _utcNow;
    private readonly Dictionary<Guid, Queue<DateTime>> _actions = new();
    private readonly object _lock = new();

    public ActionRateLimiter(int limit, TimeSpan window, Func<DateTime>? utcNow = null)
    {
        _limit = limit;
        _window = window;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>
    /// Records an action for <paramref name="userId"/> and returns 0, or, when the window is
    /// full, records nothing and returns how many seconds until the next one is allowed.
    /// </summary>
    public int TryTake(Guid userId)
    {
        var now = _utcNow();
        lock (_lock)
        {
            if (!_actions.TryGetValue(userId, out var times))
            {
                times = new Queue<DateTime>();
                _actions[userId] = times;
            }

            while (times.Count > 0 && now - times.Peek() >= _window)
            {
                times.Dequeue();
            }

            if (times.Count >= _limit)
            {
                var wait = _window - (now - times.Peek());
                return Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds));
            }

            times.Enqueue(now);
            return 0;
        }
    }
}
