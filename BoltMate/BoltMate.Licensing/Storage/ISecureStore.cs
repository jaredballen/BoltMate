using System.Threading;
using System.Threading.Tasks;

namespace BoltMate.Licensing.Storage;

public interface ISecureStore
{
    Task<string?> GetAsync(string key, CancellationToken ct = default);

    Task SetAsync(string key, string value, CancellationToken ct = default);

    Task DeleteAsync(string key, CancellationToken ct = default);
}
