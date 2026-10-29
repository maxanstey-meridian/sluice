using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Sluice.EntityFrameworkCore.Tests.Support;

/// <summary>
/// Hands EF a fresh command for each <c>ExecuteUpdate</c> or <c>ExecuteDelete</c> in place of the one it initialised,
/// with the same text, type, timeout, transaction, and parameters, as an interceptor that wraps or pools commands
/// would. Registered after Sluice, it leaves Sluice holding what it noted on a command that never runs, so Sluice sees
/// the statement end on a command it didn't see initialised.
/// </summary>
public sealed class ReplaceBulkCommand : DbCommandInterceptor
{
    public override DbCommand CommandInitialized(CommandEndEventData eventData, DbCommand result)
    {
        if (
            eventData.CommandSource
            is not (CommandSource.ExecuteUpdate or CommandSource.ExecuteDelete)
        )
        {
            return result;
        }

        var fresh = result.Connection!.CreateCommand();
        fresh.CommandText = result.CommandText;
        fresh.CommandType = result.CommandType;
        fresh.CommandTimeout = result.CommandTimeout;
        fresh.Transaction = result.Transaction;

        // A parameter belongs to one command's collection at a time, so it leaves the old one first.
        var parameters = result.Parameters.Cast<DbParameter>().ToArray();
        result.Parameters.Clear();
        fresh.Parameters.AddRange(parameters);
        return fresh;
    }
}
