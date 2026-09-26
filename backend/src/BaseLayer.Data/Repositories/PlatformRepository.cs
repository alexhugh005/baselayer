using BaseLayer.Application.Interfaces;
using BaseLayer.Domain.Entities;
using Microsoft.EntityFrameworkCore;
namespace BaseLayer.Data.Repositories;
// Single API instance: serialize SQLite write transactions. Scale-out requires DB-level leasing.
public sealed class DatabaseGate
{
    public SemaphoreSlim Semaphore { get; } = new(1, 1);
}
public sealed class PlatformRepository(PlatformDbContext db, DatabaseGate gate) : IPlatformRepository
{
    public async Task<T> TransactionAsync<T>(Func<Task<T>> operation)
    {
        await gate.Semaphore.WaitAsync();
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            var result = await operation();
            await transaction.CommitAsync();
            return result;
        }
        finally { gate.Semaphore.Release(); }
    }
    private IQueryable<Home> Homes => db.Homes.Include(h => h.Devices).Include(h => h.Commands).AsSplitQuery();
    public Task<List<Home>> HomesAsync(string ownerId) => Homes.Where(h => h.OwnerId == ownerId).ToListAsync();
    public Task<Home?> HomeAsync(Guid id) => Homes.SingleOrDefaultAsync(h => h.Id == id);
    public Task<List<Guid>> ActiveHomeIdsAsync() => db.Homes.Where(h => !h.Revoked && h.ProtectedTokens != null).Select(h => h.Id).ToListAsync();
    public Task<OAuthState?> StateAsync(string hash) => db.OAuthStates.SingleOrDefaultAsync(p => p.CodeHash == hash);
    public void Add(Home h) => db.Homes.Add(h);
    public void Remove(Home h) => db.Homes.Remove(h);
    public void Add(OAuthState p) => db.OAuthStates.Add(p);
    public async Task SaveAsync() => await db.SaveChangesAsync();
}
