using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Sluice.EntityFrameworkCore.Tests.Support;

/// <summary>
/// While <see cref="Fail"/>, fails every async query that selects only a page's key, before it runs: the lookup a read
/// by a unique column runs once its compute returns, failing as a dropped connection would.
/// </summary>
public sealed class FailKeyLookups : DbCommandInterceptor
{
    public bool Fail { get; set; } = true;

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default
    ) =>
        Fail && TrackedReads.FirstStatement(command) == "SELECT p.\"Id\""
            ? throw new InvalidOperationException("The key lookup failed.")
            : new(result);
}
