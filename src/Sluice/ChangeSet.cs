namespace Sluice;

public sealed class ChangeSet
{
    private readonly List<ResourceAddress> _changed = [];

    internal IReadOnlyList<ResourceAddress> ChangedAddresses => _changed;

    public void Changed(ResourceAddress address) => _changed.Add(address);
}
