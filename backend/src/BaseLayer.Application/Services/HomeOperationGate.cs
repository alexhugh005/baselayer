using System.Collections.Concurrent;
namespace BaseLayer.Application.Services;
// Serialize commands, permission changes, and revocation per home in this single-instance MVP.
// Network calls hold this logical lock, never a database transaction.
public sealed class HomeOperationGate
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> gates = new();
    public async Task<T> RunAsync<T>(Guid id, Func<Task<T>> action)
    {
        var gate = gates.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            return await action();
        }
        finally { gate.Release(); }
    }
    public async Task RunAsync(Guid id, Func<Task> action) => await RunAsync(id, async () => { await action(); return true; });
}
