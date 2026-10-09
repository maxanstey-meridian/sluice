using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Sluice.EntityFrameworkCore.Tests.Support;

/// <summary>
/// Fails every async command that inserts into <paramref name="table"/>, before it runs, as a constraint the
/// database enforces would. Sync commands pass through.
/// </summary>
public sealed class FailInsertsInto(string table) : DbCommandInterceptor
{
    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default
    ) =>
        command.CommandText.Contains($"INSERT INTO \"{table}\"", StringComparison.Ordinal)
            ? throw new InvalidOperationException($"The insert into {table} failed.")
            : new(result);
}
