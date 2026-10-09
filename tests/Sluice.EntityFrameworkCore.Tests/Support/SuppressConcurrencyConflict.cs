using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Sluice.EntityFrameworkCore.Tests.Support;

/// <summary>
/// Suppresses every async concurrency conflict, so the save goes on with its later batches, then runs
/// <see cref="Then"/>. Added after Sluice, it runs after Sluice has seen the conflict. Sync saves pass through.
/// </summary>
public sealed class SuppressConcurrencyConflict : SaveChangesInterceptor
{
    public Func<Task>? Then { get; set; }

    public override async ValueTask<InterceptionResult> ThrowingConcurrencyExceptionAsync(
        ConcurrencyExceptionEventData eventData,
        InterceptionResult result,
        CancellationToken cancellationToken = default
    )
    {
        if (Then is { } then)
        {
            await then();
        }

        return InterceptionResult.Suppress();
    }
}
