using System.Collections.Concurrent;
using Playground.Generated.Domain;

namespace Playground.Generated.Application;

// The visualization needs the latest materialized Dashboard per user so it can
// show the actual JSON value beside the cache event stream.
public sealed class DashboardReadState
{
    private readonly ConcurrentDictionary<string, Dashboard> _dashboards = new();

    public void Set(UserId user, Dashboard dashboard) => _dashboards[user.ResourceKey] = dashboard;

    public IReadOnlyDictionary<string, Dashboard> Snapshot() =>
        _dashboards.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
}
