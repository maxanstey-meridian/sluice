using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Sluice.EntityFrameworkCore.Tests.Support;

/// <summary>
/// Throws once the database has run a statement, before the interceptors registered after it see it succeed: a bulk
/// write that failed after it wrote, or, with <paramref name="cancel"/>, one cancelled after it wrote. EF reports the
/// first through <c>CommandFailed</c> and the second through <c>CommandCanceled</c>.
/// </summary>
public sealed class FailAfterBulkWrite(bool cancel) : DbCommandInterceptor
{
    public override int NonQueryExecuted(
        DbCommand command,
        CommandExecutedEventData eventData,
        int result
    ) => throw Failure();

    public override ValueTask<int> NonQueryExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        int result,
        CancellationToken cancellationToken = default
    ) => throw Failure();

    private Exception Failure() =>
        cancel
            ? new OperationCanceledException("The request was cancelled.")
            : new InvalidOperationException("The connection broke.");
}
