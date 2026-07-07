using System.Collections.Concurrent;
using Playground.Manual.Domain;

namespace Playground.Manual.Application;

// The visualization needs the latest materialized Dashboard per user so it can
// show the actual JSON value beside the cache event stream.
public sealed class DashboardReadState
{
    private readonly ConcurrentDictionary<string, Dashboard> _dashboards = new();

    public void Set(string user, Dashboard dashboard) => _dashboards[user] = dashboard;

    public IReadOnlyDictionary<string, Dashboard> Snapshot() =>
        _dashboards.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
}
