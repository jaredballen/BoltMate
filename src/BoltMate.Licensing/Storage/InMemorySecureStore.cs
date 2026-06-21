using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace BoltMate.Licensing.Storage;

public sealed class InMemorySecureStore : ISecureStore
{
    private readonly ConcurrentDictionary<string, string> _store = new();

    public Task<string?> GetAsync(string key, CancellationToken ct = default)
        => Task.FromResult(_store.TryGetValue(key, out var v) ? v : null);

    public Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        _store[key] = value;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        _store.TryRemove(key, out _);
        return Task.CompletedTask;
    }
}
