using Playground.Generated.Domain;
using Sluice;

namespace Playground.Generated.Application;

// Generated mode keeps the same cached projection, but gets tracked reads and
// writes from the source-generated GeneratedDashboardSluice wrapper.
public sealed class DashboardCache
{
    private const string DarkModeFlagId = "dark_mode";

    private readonly SluiceKernel _sluice;
    private readonly PlaygroundStore _store;
    private readonly GeneratedDashboardSluice _generated;
    private readonly CachedQuery<StringKey, Dashboard> _dashboardQuery;

    public DashboardCache(SluiceKernel sluice, PlaygroundStore store)
    {
        _sluice = sluice;
        _store = store;
        _generated = new GeneratedDashboardSluice(_sluice, _store);

        // The query is still hand-written. Codegen replaces the resource wrapper,
        // not the application projection.
        _dashboardQuery = new CachedQuery<StringKey, Dashboard>(
            "dashboard",
            id => id.Value,
            async (id, scope) =>
            {
                var user = await _generated.User.Get(id, scope);
                var flag = await _generated.Flag.Get(new StringKey(DarkModeFlagId), scope);
                Greeting? greeting = null;

                if (user.Role == "admin")
                {
                    greeting = await _generated.Greeting.Get(id, scope);
                }

                return new Dashboard(user, flag, greeting);
            },
            ttl: TimeSpan.FromMinutes(5)
        );
    }

    public Task<Dashboard> GetDashboard(string user, CancellationToken ct) =>
        _sluice.Get(_dashboardQuery, new StringKey(user), ct);

    public async Task<FeatureFlag> ToggleDarkMode(CancellationToken ct)
    {
        await _generated.ToggleFlag(new StringKey(DarkModeFlagId), ct);
        return await _store.GetFlag(new StringKey(DarkModeFlagId), ct);
    }

    public Task UpdateGreeting(string user, string text, CancellationToken ct) =>
        _generated.UpdateGreeting(new StringKey(user), text, ct);

    public Task FlushAll(CancellationToken ct) => _sluice.FlushAllAsync(ct);
}
