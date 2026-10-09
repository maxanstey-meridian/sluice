using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Sluice.EntityFrameworkCore.Tests.Support;

/// <summary>
/// Saves its context once more from inside an async save, as an app interceptor that writes an audit row would: from
/// <c>SavingChanges</c> when <paramref name="whileSaving"/>, otherwise from <c>SavedChanges</c>. <see cref="Once"/>
/// sets what the second save changes. Sync saves pass through.
/// </summary>
public sealed class SaveAgain(bool whileSaving) : SaveChangesInterceptor
{
    private Action? _change;

    public void Once(Action change) => _change = change;

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default
    )
    {
        if (whileSaving)
        {
            await Save(eventData.Context!, cancellationToken);
        }

        return result;
    }

    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default
    )
    {
        if (!whileSaving)
        {
            await Save(eventData.Context!, cancellationToken);
        }

        return result;
    }

    private async Task Save(DbContext context, CancellationToken cancellationToken)
    {
        if (_change is { } change)
        {
            _change = null;
            change();
            await context.SaveChangesAsync(cancellationToken);
        }
    }
}
