using Sluice;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://localhost:5300");

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.SetIsOriginAllowed(_ => true).AllowAnyMethod().AllowAnyHeader();
    });
});

var app = builder.Build();
app.UseCors();
app.UseStaticFiles();

var eventSink = new RingBufferEventSink(1000);
var sluice = new SluiceKernel(new InMemoryCacheStore(), eventSink: eventSink);
var store = new PlaygroundStore();

var userResource = new EntityResource<UserId>("user");
var userRead = userResource.Read((UserId id, CancellationToken _) => store.GetUser(id));

var userNameQuery = new CachedQuery<UserId, User>(
    "user.name",
    id => id.Value,
    async (id, scope) => await userRead.Get(id, scope),
    ttl: TimeSpan.FromMinutes(5)
);

var userSettingsQuery = new CachedQuery<UserId, User>(
    "user.settings",
    id => id.Value,
    async (id, scope) => await userRead.Get(id, scope),
    ttl: TimeSpan.FromMinutes(5)
);

var userWrite = new TrackedWrite<UserId>(sluice, userResource.For);

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

app.MapPost(
    "/api/users/{id}/read",
    async (string id) => await sluice.Get(userNameQuery, new UserId(id), CancellationToken.None)
);

app.MapPost(
    "/api/users/{id}/settings",
    async (string id) =>
        await sluice.Get(userSettingsQuery, new UserId(id), CancellationToken.None)
);

app.MapPost(
    "/api/users/{id}/invalidate",
    async (string id) =>
    {
        await sluice.Invalidate(
            new WriteEffect(userResource.For(new UserId(id))),
            CancellationToken.None
        );
    }
);

app.MapPut(
    "/api/users/{id}",
    async (string id, UpdateBody body) =>
    {
        await userWrite.Write(
            new UserId(id),
            ct => store.UpdateUser(new UserId(id), body.Name),
            CancellationToken.None
        );
    }
);

app.MapPost(
    "/api/flush",
    async () => await sluice.FlushAllAsync(CancellationToken.None)
);

app.MapPost(
    "/api/users/{id}/stampede",
    async (string id) =>
    {
        var userId = new UserId(id);
        var tasks = Enumerable
            .Range(0, 10)
            .Select(_ => sluice.Get(userNameQuery, userId, CancellationToken.None));
        await Task.WhenAll(tasks);
    }
);

app.MapFallbackToFile("index.html");

Console.WriteLine("Sluice Playground running at http://localhost:5300");
Console.WriteLine(
    "Dashboard: http://localhost:5200/?source=http://localhost:5300/sluice/events"
);
await app.RunAsync();

public sealed record UserId(string Value) : IResourceKey
{
    public string ResourceKey => Value;
}

public sealed record User(string Id, string Name, string Email);

public sealed record UpdateBody(string Name);

public sealed class PlaygroundStore
{
    private readonly Dictionary<string, User> _users = new()
    {
        ["alice"] = new User("alice", "Alice", "alice@example.com"),
        ["bob"] = new User("bob", "Bob", "bob@example.com"),
        ["charlie"] = new User("charlie", "Charlie", "charlie@example.com"),
    };

    public Task<User> GetUser(UserId id)
    {
        return Task.FromResult(_users[id.Value]);
    }

    public Task UpdateUser(UserId id, string name)
    {
        if (_users.TryGetValue(id.Value, out var user))
        {
            _users[id.Value] = user with { Name = name };
        }

        return Task.CompletedTask;
    }
}
