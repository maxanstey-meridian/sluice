namespace Sluice;

public sealed class WriteEffect(params ResourceAddress[] addresses)
{
    internal IReadOnlyList<ResourceAddress> Addresses { get; } = addresses;
}

public sealed class WriteEffect<T>(params ResourceAddress[] addresses)
{
    private readonly List<Func<T, ResourceAddress>> _resultResolvers = [];

    public WriteEffect<T> ChangesResult(Func<T, ResourceAddress> resolver)
    {
        _resultResolvers.Add(resolver);
        return this;
    }

    internal IEnumerable<ResourceAddress> Resolve(T result)
    {
        foreach (var address in addresses)
        {
            yield return address;
        }
        foreach (var resolver in _resultResolvers)
        {
            yield return resolver(result);
        }
    }
}
