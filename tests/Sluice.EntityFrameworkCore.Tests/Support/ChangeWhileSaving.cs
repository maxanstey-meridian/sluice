using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Sluice.EntityFrameworkCore.Tests.Support;

/// <summary>
/// Changes what its context saves from <c>SavingChanges</c>, sync or async like the save, as an app interceptor that
/// stamps audit columns or turns deletes into soft deletes would. Added after Sluice, it runs after Sluice's own
/// <c>SavingChanges</c>.
/// </summary>
public sealed class ChangeWhileSaving(Action<DbContext> change) : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result
    )
    {
        change(eventData.Context!);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default
    )
    {
        change(eventData.Context!);
        return new(result);
    }
}
