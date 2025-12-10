using MyRedis.Abstractions;

namespace MyRedis.Services;

/// <summary>
/// In-memory implementation of the data store
/// </summary>
public class InMemoryDataStore : IDataStore
{
    private readonly Dictionary<string, object?> _store = new();
    private readonly object _lock = new();

    public object? Get(string key)
    {
        lock (_lock)
        {
            return _store.TryGetValue(key, out var value) ? value : null;
        }
    }

    public T? Get<T>(string key) where T : class
    {
        lock (_lock)
        {
            return _store.TryGetValue(key, out var value) && value is T typedValue 
                ? typedValue 
                : null;
        }
    }

    public void Set(string key, object? value)
    {
        lock (_lock)
        {
            _store[key] = value;
        }
    }

    public bool Remove(string key)
    {
        lock (_lock)
        {
            return _store.Remove(key);
        }
    }

    public bool Exists(string key)
    {
        lock (_lock)
        {
            return _store.ContainsKey(key);
        }
    }

    public IEnumerable<string> GetAllKeys()
    {
        lock (_lock)
        {
            return _store.Keys.ToList(); // Return a copy to avoid concurrent modification
        }
    }

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _store.Count;
            }
        }
    }
}