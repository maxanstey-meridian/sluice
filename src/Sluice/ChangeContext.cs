namespace Sluice;

public sealed class ChangeContext
{
    private readonly List<ResourceAddress> _changedAddresses = new();

    public IReadOnlyList<ResourceAddress> ChangedAddresses => _changedAddresses;

    public CancellationToken CancellationToken { get; }

    public ChangeContext()
    {
        CancellationToken = CancellationToken.None;
    }

    public ChangeContext(CancellationToken cancellationToken)
    {
        CancellationToken = cancellationToken;
    }

    public async Task Apply(Func<ChangeSet, Task> work)
    {
        var changeSet = new ChangeSet();
        await work(changeSet);
        foreach (var address in changeSet.ChangedAddresses)
        {
            _changedAddresses.Add(address);
        }
    }

    public async Task<T> Apply<T>(Func<ChangeSet, Task<T>> work)
    {
        var changeSet = new ChangeSet();
        var result = await work(changeSet);
        foreach (var address in changeSet.ChangedAddresses)
        {
            _changedAddresses.Add(address);
        }
        return result;
    }

    public async Task Apply(Func<Task> work, WriteEffect effect)
    {
        await work();
        foreach (var address in effect.Resolve())
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
