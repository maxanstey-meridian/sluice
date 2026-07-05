namespace Sluice;

public abstract class TrackedResource
{
    protected async Task<T> Read<T>(
        OperationContext ctx,
        ResourceAddress address,
        Func<Task<T>> work
    )
    {
        ctx.RecordRead(address);
        return await work();
    }
}
