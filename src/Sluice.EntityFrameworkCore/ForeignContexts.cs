using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Sluice.Infrastructure;

namespace Sluice.EntityFrameworkCore;

/// <summary>
/// Refuses, inside a compute, an EF command on a context without <c>UseSluice</c>, which <see cref="TrackedReads"/>
/// never sees: a write anywhere, and a read outside a fused fetch (SLUICE-EF-015). It listens to EF's diagnostic events,
/// which EF raises for every context, interceptors or not.
/// </summary>
/// <remarks>
/// One subscription for the process, to every EF <see cref="DiagnosticListener"/> (one per internal service provider),
/// those made before it included. It asks for EF's command-initialised event whether or not a compute is running: a
/// context that finds nobody listening stops raising its command events for a second, so a command right after one
/// outside a compute would go unseen. Listening to that event alone keeps them coming, since EF clears the pause when
/// anyone does. Throwing from it reaches the caller before the connection opens. Outside a compute each command costs
/// EF one event object and this one lookup.
/// </remarks>
internal sealed class ForeignContexts
    : IObserver<DiagnosticListener>,
        IObserver<KeyValuePair<string, object?>>
{
    private static readonly string Initialized = RelationalEventId.CommandInitialized.Name!;
    private static readonly ForeignContexts _instance = new();
    private static int _subscribed;

    private ForeignContexts() { }

    /// <summary>Subscribes, once per process.</summary>
    internal static void Subscribe()
    {
        if (Interlocked.Exchange(ref _subscribed, 1) == 0)
        {
            DiagnosticListener.AllListeners.Subscribe(_instance);
        }
    }

    public void OnNext(DiagnosticListener listener)
    {
        if (listener.Name == DbLoggerCategory.Name)
        {
            listener.Subscribe(this, static name => name == Initialized);
        }
    }

    public void OnNext(KeyValuePair<string, object?> value)
    {
        if (
            value.Key == Initialized
            && ActiveCompute.Reads is { } reads
            && value.Value is CommandEndEventData { Context: { } context } data
            && !TrackedReads.IsOn(context.GetService<IDbContextOptions>())
            && (TrackedReads.IsWrite(data.CommandSource) || !ActiveCompute.InFetch(reads))
        )
        {
            var name = context.GetType().Name;
            throw ActiveCompute.Violate(
                reads,
                TrackedReads.IsWrite(data.CommandSource)
                    ? TrackedReads.WriteInCompute(data.Command)
                    : new InvalidOperationException(
                        $"An EF command ({TrackedReads.Statement(data.Command)}) ran inside a compute on a {name}, "
                            + "which isn't configured with UseSluice, so Sluice can't record what it read and the cached "
                            + $"value would go stale. Add .UseSluice(services) to {name}'s options and query it through "
                            + $"read.Db<{name}>(), or run it in the fetch of read.From(dependency, fetch) with a Source "
                            + "you declare."
                    )
            );
        }
    }

    public void OnCompleted() { }

    public void OnError(Exception error) { }
}
