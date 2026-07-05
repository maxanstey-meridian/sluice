namespace Sluice;

public sealed class ChangeContext(CancellationToken cancellationToken)
{
    private readonly List<ResourceAddress> _changedAddresses = [];

    public IReadOnlyList<ResourceAddress> ChangedAddresses => _changedAddresses;

    public CancellationToken CancellationToken { get; } = cancellationToken;

    public async Task Apply(Func<Task> work, WriteEffect effect)
    {
        await work();
        foreach (var address in effect.Addresses)
        {
            _changedAddresses.Add(address);
        }
    }

    public async Task<T> Apply<T>(Func<Task<T>> work, WriteEffect<T> effect)
    {
        var result = await work();
        foreach (var address in effect.Resolve(result))
        {
            _changedAddresses.Add(address);
        }
        return result;
    }
}
