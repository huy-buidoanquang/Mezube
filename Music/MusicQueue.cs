using Mezube.Domain.Entities;

namespace Mezube.Music;

public sealed class MusicQueue
{
    private readonly object _gate = new();
    private readonly Queue<TrackInfoEntity> _tracks = new();

    public TrackInfoEntity? Current { get; private set; }
    public int Count
    {
        get { lock (_gate) return _tracks.Count; }
    }

    public IReadOnlyList<TrackInfoEntity> Snapshot()
    {
        lock (_gate)
        {
            return _tracks.ToArray();
        }
    }

    public void Enqueue(TrackInfoEntity track)
    {
        lock (_gate)
        {
            _tracks.Enqueue(track);
        }
    }

    public TrackInfoEntity? PeekNext()
    {
        lock (_gate)
        {
            return _tracks.Count > 0 ? _tracks.Peek() : null;
        }
    }

    public TrackInfoEntity? TryDequeueNext()
    {
        lock (_gate)
        {
            if (_tracks.Count == 0)
            {
                Current = null;
                return null;
            }

            Current = _tracks.Dequeue();
            return Current;
        }
    }

    public void Clear(bool clearCurrent = true)
    {
        lock (_gate)
        {
            _tracks.Clear();
            if (clearCurrent)
            {
                Current = null;
            }
        }
    }

    public void ClearCurrent()
    {
        lock (_gate)
        {
            Current = null;
        }
    }
}
