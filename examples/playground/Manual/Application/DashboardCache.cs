using Playground.Manual.Domain;
using Sluice;

namespace Playground.Manual.Application;

// This is the hand-written Sluice layer for the playground.
// It names the resources, derives tracked reads/writes, and composes them into
// the cached dashboard projection that the UI exercises.
public sealed class DashboardCache
{
    private const string DarkModeFlagId = "dark_mode";

    private readonly SluiceKernel _sluice;
    private readonly PlaygroundStore _store;
    private readonly CachedQuery<StringKey, Dashboard> _dashboardQuery;
    private readonly TrackedWrite<StringKey> _flagWrite;
    private readonly TrackedWrite<StringKey> _greetingWrite;

    public DashboardCache(SluiceKernel sluice, PlaygroundStore store)
    {
        _sluice = sluice;
        _store = store;

        // Resource definitions are pure identity: kind + name + key type.
        // The same resource name must be used by reads and writes so Sluice can
        // intersect "what was read" with "what changed" during invalidation.
        var userResource = new EntityResource<StringKey>("user");
        var flagResource = new EntityResource<StringKey>("flag");
        var greetingResource = new EntityResource<StringKey>("greeting");

        // .Read(storeMethod) pairs a resource address with the actual fetch.
        // .Get(key, scope) below records that address before calling the store.
        var userRead = userResource.Read((id, _) => _store.GetUser(id.Value));
        var flagRead = flagResource.Read((id, _) => _store.GetFlag(id.Value));
        var greetingRead = greetingResource.Read((id, _) => _store.GetGreeting(id.Value));

        // The cached query is the projection users read from the playground.
        // The cache key becomes dashboard:v1:"alice" or dashboard:v1:"bob".
        _dashboardQuery = new CachedQuery<StringKey, Dashboard>(
            "dashboard",
            id => id.Value,
            async (id, scope) =>
            {
                // Both dashboards depend on their user row and the shared flag.
                var user = await userRead.Get(id, scope);
                var flag = await flagRead.Get(new StringKey(DarkModeFlagId), scope);
                Greeting? greeting = null;

                // Alice is admin, so only Alice's dashboard observes greeting:alice.
                // Bob never reads it, so a greeting write should not evict Bob.
                if (user.Role == "admin")
                {
                    greeting = await greetingRead.Get(id, scope);
                }

                return new Dashboard(user, flag, greeting);
            },
            ttl: TimeSpan.FromMinutes(5)
        );

        // Writes declare which resource address changed.
        // Sluice evicts cached entries whose recorded reads include that address.
        _flagWrite = new TrackedWrite<StringKey>(_sluice, flagResource.For);
        _greetingWrite = new TrackedWrite<StringKey>(_sluice, greetingResource.For);
    }

    // Cache miss: compute the dashboard, record observed resource reads, store it.
    // Cache hit: return the stored Dashboard without calling PlaygroundStore.
    public Task<Dashboard> GetDashboard(string user, CancellationToken ct) =>
        _sluice.Get(_dashboardQuery, new StringKey(user), ct);

    public async Task<FeatureFlag> ToggleDarkMode(CancellationToken ct)
    {
        // dark_mode is shared, so both dashboard:v1:alice and dashboard:v1:bob
        // are affected after they have observed flag:dark_mode.
        await _flagWrite.Write(
            new StringKey(DarkModeFlagId),
            _ => _store.ToggleFlag(DarkModeFlagId),
            ct
        );

        return await _store.GetFlag(DarkModeFlagId);
    }

    // greeting:{user} is selective. In the seeded data only Alice reads a greeting,
    // so updating Alice's greeting evicts Alice but leaves Bob cached.
    public Task UpdateGreeting(string user, string text, CancellationToken ct) =>
        _greetingWrite.Write(new StringKey(user), _ => _store.UpdateGreeting(user, text), ct);

    // Flush is the broad escape hatch: clear cache entries and dependency edges.
    public Task FlushAll(CancellationToken ct) => _sluice.FlushAllAsync(ct);
}
