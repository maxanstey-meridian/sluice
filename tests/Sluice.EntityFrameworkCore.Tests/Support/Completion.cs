using System.Transactions;

namespace Sluice.EntityFrameworkCore.Tests.Support;

public static class Completion
{
    /// <summary>
    /// Completes once <paramref name="transaction"/> has, and once every completion handler subscribed before this
    /// call has run: handlers run in order. It can complete after <c>TransactionScope.Dispose</c> returns.
    /// </summary>
    public static Task Of(Transaction transaction)
    {
        var completed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        transaction.TransactionCompleted += (_, _) => completed.TrySetResult();

        // A bound, not a wait: only a lost completion would reach it.
        return completed.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
