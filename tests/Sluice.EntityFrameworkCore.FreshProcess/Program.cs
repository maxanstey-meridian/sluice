using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Sluice;

// SLUICE-EF-015's subscription to EF's diagnostic events starts once per process, at AddSluiceEntityFrameworkCore, and
// takes in the listeners EF made before it, one per internal service provider. So, in a process nothing else has run
// in: a context without UseSluice reads inside a compute unrefused before the call, and right after it, with no
// UseSluice context or compute in between, a context with the same options, whose listener EF made before the call,
// is refused before its command runs. Its table doesn't exist, so a read that isn't refused fails on the database.
// The second context is another instance, since one that has run a command nobody listened to pauses its command
// events for a second. Takes the database's connection string; prints what it saw and exits 0, or says what went
// wrong and exits 1.
var connectionString = args[0];
var query = new Query<int, int>("fresh-process-read");
await using var provider = new ServiceCollection().AddSluice().BuildServiceProvider();
var cache = provider.GetRequiredService<SluiceCache>();

await using (var before = new ForeignDb(connectionString))
{
    if (await ReadInCompute(before, 1) is not PostgresException)
    {
        return Fail("a compute's read wasn't run, unrefused, before AddSluiceEntityFrameworkCore");
    }
}

Console.WriteLine("not refused before the call");
new ServiceCollection().AddSluiceEntityFrameworkCore();

await using var after = new ForeignDb(connectionString);
var refused = await ReadInCompute(after, 2);
return
    refused is InvalidOperationException
    && refused.Message.Contains("isn't configured with UseSluice")
    ? Pass()
    : Fail($"a compute's read wasn't refused after AddSluiceEntityFrameworkCore: {refused}");

async Task<Exception?> ReadInCompute(ForeignDb db, int key)
{
    try
    {
        await cache.GetOrSetAsync(query, key, async (_, ct) => await db.Rows.CountAsync(ct));
        return null;
    }
    catch (Exception exception)
    {
        return exception;
    }
}

static int Pass()
{
    Console.WriteLine("refused after the call");
    return 0;
}

static int Fail(string why)
{
    Console.Error.WriteLine(why);
    return 1;
}

internal sealed class ForeignDb(string connectionString) : DbContext
{
    public DbSet<Row> Rows => Set<Row>();

    protected override void OnConfiguring(DbContextOptionsBuilder options) =>
        options.UseNpgsql(connectionString);
}

internal sealed class Row
{
    public int Id { get; set; }
}
