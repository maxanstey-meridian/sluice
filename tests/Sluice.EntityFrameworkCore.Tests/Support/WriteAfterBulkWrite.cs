using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Sluice.EntityFrameworkCore.Tests.Support;

/// <summary>
/// Runs <see cref="Write"/> once, from inside the next statement's <c>NonQueryExecuted</c>, on the same flow, as an app
/// interceptor that writes an audit row in bulk would. Added after Sluice, Sluice has seen the statement end first;
/// added before it, the write ends before Sluice sees the statement end.
/// </summary>
public sealed class WriteAfterBulkWrite : DbCommandInterceptor
{
    public Action? Write { get; set; }

    public override int NonQueryExecuted(
        DbCommand command,
        CommandExecutedEventData eventData,
        int result
    )
    {
        Run();
        return result;
    }

    public override ValueTask<int> NonQueryExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        int result,
        CancellationToken cancellationToken = default
    )
    {
        Run();
        return new(result);
    }

    private void Run()
    {
        if (Write is { } write)
        {
            Write = null;
            write();
        }
    }
}
