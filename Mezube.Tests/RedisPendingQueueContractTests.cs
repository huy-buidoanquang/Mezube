namespace Mezube.Tests;

public sealed class RedisPendingQueueContractTests
{
    [Fact]
    public void EnsureCurrent_lpops_only_when_current_empty()
    {
        var q = new PendingOnlyQueue();
        q.Enqueue("a");
        q.Enqueue("b");
        Assert.Equal("a", q.EnsureCurrent());
        Assert.Equal("a", q.Current);
        Assert.Equal(["b"], q.Pending);
        Assert.Equal("a", q.EnsureCurrent());
        Assert.Equal(["b"], q.Pending);
    }

    [Fact]
    public void EnqueueFront_pushes_left()
    {
        var q = new PendingOnlyQueue();
        q.Enqueue("a");
        q.EnqueueFront("x");
        Assert.Equal("x", q.EnsureCurrent());
        Assert.Equal(["a"], q.Pending);
    }

    [Fact]
    public void Restore_does_not_duplicate_current_into_pending()
    {
        var q = new PendingOnlyQueue();
        q.Current = "playing";
        q.Enqueue("next");
        var restored = new List<string>();
        if (q.Current is not null)
        {
            restored.Add(q.Current);
        }

        restored.AddRange(q.Pending);
        Assert.Equal(["playing", "next"], restored);
        Assert.DoesNotContain(q.Pending, x => x == q.Current);
    }

    [Fact]
    public void ClearSession_drops_current_and_pending()
    {
        var q = new PendingOnlyQueue();
        q.Current = "playing";
        q.Enqueue("next");
        q.Clear();
        Assert.Null(q.Current);
        Assert.Empty(q.Pending);
        Assert.Null(q.EnsureCurrent());
    }

    private sealed class PendingOnlyQueue
    {
        public string? Current { get; set; }
        public List<string> Pending { get; } = [];

        public void Enqueue(string item) => Pending.Add(item);

        public void EnqueueFront(string item) => Pending.Insert(0, item);

        public string? EnsureCurrent()
        {
            if (!string.IsNullOrEmpty(Current))
            {
                return Current;
            }

            if (Pending.Count == 0)
            {
                return null;
            }

            Current = Pending[0];
            Pending.RemoveAt(0);
            return Current;
        }

        public void Clear()
        {
            Current = null;
            Pending.Clear();
        }
    }
}
