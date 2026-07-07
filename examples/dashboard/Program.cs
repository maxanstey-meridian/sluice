using Sluice;

var eventSink = new RingBufferEventSink(1000);
var sluice = new SluiceKernel(new InMemoryCacheStore(), eventSink: eventSink);
var store = new DemoUserStore();

var userNameQuery = new CachedQuery<UserId, DemoUser>(
    "user.name",
    id => id.Value,
    async (id, scope) =>
    {
        var address = new ResourceAddress(ResourceKind.Entity, "user", id.Value);
        return await scope.Track(
            address,
            async ct =>
            {
                var user = await store.GetUser(id);
                return user;
            }
        );
    },
    ttl: TimeSpan.FromSeconds(30)
);

var userSettingsQuery = new CachedQuery<UserId, DemoUser>(
    "user.settings",
    id => id.Value,
    async (id, scope) =>
    {
        var address = new ResourceAddress(ResourceKind.Entity, "user", id.Value);
        return await scope.Track(
            address,
            async ct =>
            {
                var user = await store.GetUserSettings(id);
                return user;
            }
        );
    },
    ttl: TimeSpan.FromSeconds(30)
);

var userIds = new[] { new UserId("alice"), new UserId("bob"), new UserId("charlie") };

_ = Task.Run(async () =>
{
    var rng = Random.Shared;
    while (true)
    {
        await Task.Delay(TimeSpan.FromSeconds(rng.Next(2, 5)));

        var userId = userIds[rng.Next(userIds.Length)];

        if (rng.Next(4) == 0)
        {
            var effect = new WriteEffect([
                new ResourceAddress(ResourceKind.Entity, "user", userId.Value),
            ]);
            await sluice.Invalidate(effect, CancellationToken.None);
        }
        else
        {
            if (rng.Next(2) == 0)
            {
                await sluice.Get(userNameQuery, userId, CancellationToken.None);
            }
            else
            {
                await sluice.Get(userSettingsQuery, userId, CancellationToken.None);
            }
        }
    }
});

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://localhost:5200");

var app = builder.Build();
app.UseStaticFiles();

app.MapGet(
    "/sluice/events",
    (long? since) =>
    {
        return eventSink
            .GetEventsSince(since ?? 0)
            .Select(s => new
            {
                s.Seq,
                s.Event.Timestamp,
                s.Event.Type,
                s.Event.Operation,
                s.Event.EntryKey,
                s.Event.ResourceName,
                s.Event.DurationMs,
                s.Event.Detail,
            });
    }
);

app.MapFallbackToFile("index.html");

Console.WriteLine("Sluice Dashboard running at http://localhost:5200");
await app.RunAsync();

public sealed record UserId(string Value)
{
    public override string ToString() => Value;
}

public sealed record DemoUser(string Name, string Email, string Theme, bool DarkMode);

public sealed class DemoUserStore
{
    private readonly Dictionary<string, DemoUser> _users = new()
    {
        ["alice"] = new DemoUser("Alice", "alice@example.com", "ocean", false),
        ["bob"] = new DemoUser("Bob", "bob@example.com", "forest", true),
        ["charlie"] = new DemoUser("Charlie", "charlie@example.com", "sunset", false),
    };

    public Task<DemoUser> GetUser(UserId id)
    {
        return Task.FromResult(_users[id.Value]);
    }

    public Task<DemoUser> GetUserSettings(UserId id)
    {
        return Task.FromResult(_users[id.Value]);
    }
}
