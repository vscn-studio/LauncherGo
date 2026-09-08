namespace ServerMap.Util;

// Reset never waits for a loader (which may be blocked on SQLite). In-flight
// readers can finish against the old generation but cannot republish it.
internal sealed class ResettableCache<TKey, TValue>(int capacity) where TKey : notnull
{
    private sealed class Generation(int size)
    {
        internal readonly object Gate = new();
        internal readonly LruCache<TKey, TValue> Values = new(size);
    }

    private Generation generation = new(capacity);

    public TValue GetOrAdd(TKey key, Func<TKey, TValue> load)
    {
        var current = Volatile.Read(ref generation);
        lock (current.Gate)
        {
            if (current.Values.TryGet(key, out var value)) return value;
            value = load(key);
            current.Values.Set(key, value);
            return value;
        }
    }

    public void Reset() => Interlocked.Exchange(ref generation, new Generation(capacity));
}
