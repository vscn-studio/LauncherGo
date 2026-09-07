namespace ServerMap.Util;

public sealed class LruCache<TKey, TValue>(int capacity) where TKey : notnull
{
    private readonly object gate = new();
    private readonly int max = Math.Max(1, capacity);
    private readonly Dictionary<TKey, LinkedListNode<(TKey Key, TValue Value)>> map = new();
    private readonly LinkedList<(TKey Key, TValue Value)> list = new();
    public bool TryGet(TKey key, out TValue value)
    {
        lock (gate)
        {
            if (!map.TryGetValue(key, out var node)) { value = default!; return false; }
            list.Remove(node); list.AddFirst(node); value = node.Value.Value; return true;
        }
    }
    public void Set(TKey key, TValue value)
    {
        lock (gate)
        {
            if (map.TryGetValue(key, out var old)) list.Remove(old);
            var node = new LinkedListNode<(TKey, TValue)>((key, value));
            list.AddFirst(node); map[key] = node;
            while (map.Count > max && list.Last is { } last) { list.RemoveLast(); map.Remove(last.Value.Key); }
        }
    }
    public void Clear() { lock (gate) { map.Clear(); list.Clear(); } }
}
