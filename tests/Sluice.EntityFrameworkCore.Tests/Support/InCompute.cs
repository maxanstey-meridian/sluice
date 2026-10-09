namespace Sluice.EntityFrameworkCore.Tests.Support;

public static class InCompute
{
    private static readonly Query<string, int> _query = new("ef-in-compute");

    /// <summary>
    /// Runs <paramref name="read"/> inside a compute on a node of its own, so nothing an earlier call cached can stand
    /// in for it.
    /// </summary>
    public static async Task Run(Func<SluiceReads, CancellationToken, Task> read)
    {
        await using var node = new SluiceNode();
        await node.Cache.GetOrSetAsync(
            _query,
            "key",
            async (reads, ct) =>
            {
                await read(reads, ct);
                return 0;
            }
        );
    }
}
