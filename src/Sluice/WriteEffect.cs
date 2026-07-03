namespace Sluice;

public sealed class WriteEffect
{
    private readonly List<ResourceAddress> _addresses = new();

    public static WriteEffect For() => new();

    public WriteEffect Changes(ResourceAddress address)
    {
        _addresses.Add(address);
        return this;
    }

    internal IEnumerable<ResourceAddress> Resolve()
    {
        return _addresses;
    }
}

public sealed class WriteEffect<T>
{
    private readonly List<ResourceAddress> _addresses = new();
    private readonly List<Func<T, ResourceAddress>> _resultResolvers = new();

    public static WriteEffect<T> For() => new();

    public WriteEffect<T> Changes(ResourceAddress address)
    {
        _addresses.Add(address);
        return this;
    }

    public WriteEffect<T> ChangesResult(Func<T, ResourceAddress> resolver)
    {
        _resultResolvers.Add(resolver);
        return this;
    }

    internal IEnumerable<ResourceAddress> Resolve(T result)
    {
        foreach (var address in _addresses)
        {
            yield return address;
        }
        foreach (var resolver in _resultResolvers)
        {
            yield return resolver(result);
        }
    }
}
