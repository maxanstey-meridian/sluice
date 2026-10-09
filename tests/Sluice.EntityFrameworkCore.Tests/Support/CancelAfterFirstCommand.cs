using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Sluice.EntityFrameworkCore.Tests.Support;

/// <summary>Cancels <paramref name="request"/> once the first command has run, so the next one is cancelled.</summary>
public sealed class CancelAfterFirstCommand(CancellationTokenSource request) : DbCommandInterceptor
{
    public override async ValueTask<DbDataReader> ReaderExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        DbDataReader result,
        CancellationToken cancellationToken = default
    )
    {
        await request.CancelAsync();
        return result;
    }
}
