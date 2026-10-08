using Microsoft.Extensions.DependencyInjection;

namespace Sluice.EntityFrameworkCore.Tests.Support;

/// <summary>One application instance: a service provider with <c>AddSluice</c>, as an app would build it.</summary>
public sealed class SluiceNode : IAsyncDisposable
{
    private readonly ServiceProvider _provider;

    public SluiceNode()
    {
        _provider = new ServiceCollection().AddSluice().BuildServiceProvider();
        Cache = _provider.GetRequiredService<SluiceCache>();
    }

    public SluiceCache Cache { get; }

    public ValueTask DisposeAsync() => _provider.DisposeAsync();
}
