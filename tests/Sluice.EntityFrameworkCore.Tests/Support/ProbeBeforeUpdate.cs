using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Sluice.EntityFrameworkCore.Tests.Support;

/// <summary>
/// Runs <see cref="Probe"/> once, just before the first async <c>UPDATE</c> command runs: a reader that gets in while
/// a write is on its way to the database. Sync commands pass through.
/// </summary>
public sealed class ProbeBeforeUpdate : DbCommandInterceptor
{
    public Func<Task>? Probe { get; set; }

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default
    )
    {
        await RunIfUpdate(command);
        return result;
    }

    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default
    )
    {
        await RunIfUpdate(command);
        return result;
    }

    private async Task RunIfUpdate(DbCommand command)
    {
        if (
            Probe is { } probe
            && command.CommandText.TrimStart().StartsWith("UPDATE", StringComparison.Ordinal)
        )
        {
            Probe = null;
            await probe();
        }
    }
}
