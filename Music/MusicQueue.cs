using Mezube.Domain.Entities;

namespace Mezube.Music;

public sealed class MusicQueue
{
    private readonly object _gate = new();
    private readonly Queue<QueuedPlay> _items = new();

    public QueuedPlay? CurrentItem { get; private set; }

    public TrackInfoEntity? Current => CurrentItem?.Track;

    public int Count
    {
        get { lock (_gate) return _items.Count; }
    }

    /// <summary>Current track (if any) plus pending queue length.</summary>
    public int TotalCount
    {
        get
        {
            lock (_gate)
            {
                return _items.Count + (CurrentItem is null ? 0 : 1);
            }
        }
    }

    public IReadOnlyList<QueuedPlay> Snapshot()
    {
        lock (_gate)
        {
            return _items.ToArray();
        }
    }

    public void Enqueue(QueuedPlay item)
    {
        ArgumentNullException.ThrowIfNull(item);
        lock (_gate)
        {
            _items.Enqueue(item);
        }
    }

    public bool TryRemovePending(Func<QueuedPlay, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        lock (_gate)
        {
            if (_items.Count == 0)
            {
                return false;
            }

            var kept = new Queue<QueuedPlay>(_items.Count);
            var removed = false;
            while (_items.Count > 0)
            {
                var item = _items.Dequeue();
                if (!removed && predicate(item))
                {
                    removed = true;
                    continue;
                }

                kept.Enqueue(item);
            }

            while (kept.Count > 0)
            {
                _items.Enqueue(kept.Dequeue());
            }

            return removed;
        }
    }

    public QueuedPlay? PeekNext()
    {
        lock (_gate)
        {
            return _items.Count > 0 ? _items.Peek() : null;
        }
    }

    public QueuedPlay? TryDequeueNext()
    {
        lock (_gate)
        {
            if (_items.Count == 0)
            {
                CurrentItem = null;
                return null;
            }

            CurrentItem = _items.Dequeue();
            return CurrentItem;
        }
    }

    public void Clear(bool clearCurrent = true)
    {
        lock (_gate)
        {
            _items.Clear();
            if (clearCurrent)
            {
                CurrentItem = null;
            }
        }
    }

    public void ClearCurrent()
    {
        lock (_gate)
        {
            CurrentItem = null;
        }
    }
}
