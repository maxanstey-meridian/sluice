namespace Sluice;

internal sealed class ReadScope(OperationContext ctx) : IReadScope
{
    public TimeProvider Clock => ctx.Clock;
    public CancellationToken CancellationToken => ctx.CancellationToken;

    public async Task<T> Track<T>(ResourceAddress address, Func<CancellationToken, Task<T>> work)
    {
        ctx.RecordRead(address);
        return await work(ctx.CancellationToken);
    }

    public Task<T> Track<T>(ResourceAddress address, Func<Task<T>> work) =>
        Track<T>(address, _ => work());
}
