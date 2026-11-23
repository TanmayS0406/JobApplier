namespace JobApplier.Services;

/// <summary>
/// Lets at most N jobs run at once, where N is read again every time (Settings -> "Jobs at the same time" applies without a restart),
/// and hands the free slots out in the order the jobs asked for them.
/// Hangfire has a fixed number of workers, so this is what keeps the number of browser windows at the user's setting.
/// </summary>
internal sealed class SlotGate
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

    private readonly object _lock = new();
    private readonly LinkedList<object> _waiting = new();
    private int _inUse;

    public int InUse
    {
        get { lock (_lock) return _inUse; }
    }

    public int Waiting
    {
        get { lock (_lock) return _waiting.Count; }
    }

    /// <summary>Waits for a slot. Throws <see cref="OperationCanceledException"/> (and gives up its place in line) when cancelled.</summary>
    public async Task<Lease> EnterAsync(Func<int> limit, CancellationToken cancellationToken)
    {
        LinkedListNode<object> place;
        lock (_lock) place = _waiting.AddLast(new object());

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (_lock)
                {
                    if (_waiting.First == place && _inUse < Math.Max(1, limit()))
                    {
                        _waiting.Remove(place);
                        _inUse++;
                        return new Lease(this);
                    }
                }
                await Task.Delay(PollInterval, cancellationToken);
            }
        }
        catch
        {
            lock (_lock)
            {
                if (place.List != null) _waiting.Remove(place);
            }
            throw;
        }
    }

    private void Release()
    {
        lock (_lock) _inUse--;
    }

    public sealed class Lease : IDisposable
    {
        private SlotGate? _gate;

        internal Lease(SlotGate gate) => _gate = gate;

        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }
}
