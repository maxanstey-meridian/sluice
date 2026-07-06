namespace Sluice;

public interface IStampedeCoordinator
{
    public Task<IRefreshLease?> TryAcquireAsync(
        string entryKey,
        TimeSpan leaseTtl,
        CancellationToken ct
    );

    public Task ClearAsync(CancellationToken ct);
}

public interface IRefreshLease : IAsyncDisposable
{
    public string EntryKey { get; }
    public string Token { get; }
}
