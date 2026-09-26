using BaseLayer.Domain.Entities;
namespace BaseLayer.Application.Interfaces;

public interface IPlatformRepository
{
    Task<T> TransactionAsync<T>(Func<Task<T>> operation);
    Task<List<Home>> HomesAsync(string ownerId);
    Task<Home?> HomeAsync(Guid id);
    Task<List<Guid>> ActiveHomeIdsAsync();
    Task<OAuthState?> StateAsync(string hash);
    void Add(Home home);
    void Remove(Home home);
    void Add(OAuthState state);
    Task SaveAsync();
}
