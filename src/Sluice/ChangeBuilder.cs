namespace Sluice;

public sealed class ChangeBuilder
{
    private readonly List<ResourceAddress> _addresses = [];

    public ChangeBuilder Changed(ResourceAddress address)
    {
        _addresses.Add(address);
        return this;
    }

    internal WriteEffect ToWriteEffect() => new(_addresses.ToArray());
}

public sealed class ChangeBuilder<T>
{
    private readonly List<ResourceAddress> _addresses = [];
    private readonly List<Func<T, ResourceAddress>> _resolvers = [];

    public ChangeBuilder<T> Changed(ResourceAddress address)
    {
        _addresses.Add(address);
        return this;
    }

    public ChangeBuilder<T> Changed(Func<T, ResourceAddress> resolver)
    {
        _resolvers.Add(resolver);
        return this;
    }

    internal WriteEffect<T> ToWriteEffect()
    {
        var effect = new WriteEffect<T>(_addresses.ToArray());
        foreach (var resolver in _resolvers)
        {
            effect.ChangesResult(resolver);
        }
        return effect;
    }
}
