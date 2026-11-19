using System;
using System.Threading;
using System.Threading.Tasks;

namespace Moonfin.Romm;

/// <summary>
/// Keeps the last good snapshot of a RomM library. Requests read the snapshot without waiting
/// on RomM, a stale one is refreshed in the background, and a failed refresh keeps the old
/// snapshot, so RomM going offline never empties the library (or its cached artwork).
/// </summary>
public sealed class RommInventory
{
    private readonly Func<CancellationToken, Task<RommSnapshot>> _load;
    private readonly TimeSpan _maxAge;
    private readonly TimeSpan _retryAfterFailure;
    private readonly TimeSpan _firstLoadWait;
    private readonly TimeSpan _loadTimeout;
    private readonly object _gate = new object();
    private RommSnapshot? _snapshot;
    private string? _signature;
    private Task? _refresh;
    private DateTime _lastFailureUtc = DateTime.MinValue;

    public RommInventory(
        Func<CancellationToken, Task<RommSnapshot>> load,
        TimeSpan? maxAge = null,
        TimeSpan? retryAfterFailure = null,
        TimeSpan? firstLoadWait = null,
        TimeSpan? loadTimeout = null)
    {
        _load = load;
        _maxAge = maxAge ?? TimeSpan.FromMinutes(5);
        _retryAfterFailure = retryAfterFailure ?? TimeSpan.FromSeconds(30);
        _firstLoadWait = firstLoadWait ?? TimeSpan.FromSeconds(10);
        _loadTimeout = loadTimeout ?? TimeSpan.FromMinutes(2);
    }

    /// <summary>Raised after a load that changed what clients see, including the first one.</summary>
    public event Action? Changed;

    public RommSnapshot? Current => Volatile.Read(ref _snapshot);

    public string? LastError { get; private set; }

    /// <summary>
    /// The current snapshot. When none has loaded yet this waits briefly for the first load,
    /// so the first browse after a restart still lists games.
    /// </summary>
    public RommSnapshot? Get()
    {
        var snapshot = Current;
        var pending = snapshot == null || DateTime.UtcNow - snapshot.LoadedUtc > _maxAge
            ? StartRefresh()
            : null;
        if (snapshot != null || pending == null)
        {
            return snapshot;
        }

        try
        {
            pending.Wait(_firstLoadWait);
        }
        catch (AggregateException)
        {
            // RefreshCoreAsync records its own failure. The caller just sees no snapshot.
        }

        return Current;
    }

    /// <summary>
    /// Starts a background load unless one is running or the last one failed moments ago.
    /// Returns the running load, or null when none was started.
    /// </summary>
    public Task? StartRefresh()
    {
        lock (_gate)
        {
            if (_refresh != null)
            {
                return _refresh;
            }

            if (DateTime.UtcNow - _lastFailureUtc < _retryAfterFailure)
            {
                return null;
            }

            _refresh = Task.Run(RefreshCoreAsync);
            return _refresh;
        }
    }

    private async Task RefreshCoreAsync()
    {
        var changed = false;
        try
        {
            using var timeout = new CancellationTokenSource(_loadTimeout);
            var loaded = await _load(timeout.Token).ConfigureAwait(false);
            var signature = loaded.ContentSignature();
            changed = !string.Equals(signature, _signature, StringComparison.Ordinal);
            _signature = signature;
            Volatile.Write(ref _snapshot, loaded);
            LastError = null;
        }
        catch (Exception ex)
        {
            LastError = ex is OperationCanceledException ? "RomM didn't answer in time" : ex.Message;
            _lastFailureUtc = DateTime.UtcNow;
        }
        finally
        {
            lock (_gate)
            {
                _refresh = null;
            }
        }

        if (changed)
        {
            try
            {
                Changed?.Invoke();
            }
            catch (Exception)
            {
                // A listener failing must not turn a good load into a failed one.
            }
        }
    }
}
