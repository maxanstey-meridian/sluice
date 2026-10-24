using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Sluice.EntityFrameworkCore.Tests.Support;

/// <summary>
/// Stops EF saving, and reports <paramref name="rows"/> rows saved instead, as a provider or interceptor that writes
/// without a <c>SaveChanges</c> command would look to Sluice.
/// </summary>
public sealed class SuppressSave(int rows) : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result
    ) => InterceptionResult<int>.SuppressWithResult(rows);

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default
    ) => new(InterceptionResult<int>.SuppressWithResult(rows));
}
