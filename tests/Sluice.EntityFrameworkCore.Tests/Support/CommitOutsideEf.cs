using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;

namespace Sluice.EntityFrameworkCore.Tests.Support;

/// <summary>
/// Once armed, commits the context's EF transaction on the <see cref="DbTransaction"/> itself, outside EF, as soon as
/// the next save or bulk write has run, as app code holding the raw transaction might. Added before Sluice, it commits
/// before Sluice sees the write complete. Async writes only.
/// </summary>
public sealed class CommitOutsideEf : SaveChangesInterceptor, IDbCommandInterceptor
{
    private bool _armed;

    public void Arm() => _armed = true;

    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default
    )
    {
        await Commit(eventData.Context!);
        return result;
    }

    public async ValueTask<int> NonQueryExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        int result,
        CancellationToken cancellationToken = default
    )
    {
        await Commit(eventData.Context!);
        return result;
    }

    private async Task Commit(DbContext context)
    {
        if (_armed && context.Database.CurrentTransaction is { } transaction)
        {
            _armed = false;
            await transaction.GetDbTransaction().CommitAsync();
        }
    }
}
