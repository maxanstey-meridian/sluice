using Microsoft.EntityFrameworkCore;
using Sluice;

namespace Playground;

// What the playground page can do: read a dashboard, and make the two kinds of write.
public sealed class DashboardActions(
    SluiceCache cache,
    AppDb db,
    GreetingApi greetings,
    ComputeCounts computes
)
{
    public ValueTask<Dashboard> GetDashboard(UserId userId, CancellationToken ct) =>
        cache.GetOrSetAsync(
            Dashboards.ForUser,
            userId,
            async (read, ct) =>
            {
                computes.Add(userId);

                // EF Core: query the compute's own context, not an injected one.
                // Each query records what it reads.
                var computeDb = read.Db<AppDb>();
                var user = await computeDb.Users.SingleAsync(u => u.Id == userId, ct);
                var darkMode = await computeDb
                    .Flags.Where(flag => flag.Id == FlagId.DarkMode)
                    .Select(flag => flag.Enabled)
                    .SingleAsync(ct);

                // Any other source: record the dependency and fetch in one call.
                // Only admins read a greeting, so only admins depend on one.
                string? greeting = null;
                if (user.Role == Role.Admin)
                {
                    greeting = await read.From(
                        Dashboards.Greeting.For(userId),
                        ct => greetings.GetAsync(userId, ct)
                    );
                }

                return new Dashboard(user.Name, darkMode, greeting);
            },
            ct
        );

    public async Task<bool> ToggleDarkMode(CancellationToken ct)
    {
        var flag = await db.Flags.SingleAsync(flag => flag.Id == FlagId.DarkMode, ct);
        flag.Enabled = !flag.Enabled;

        // Once this commits, every dashboard that read the flag is evicted. No Sluice code needed.
        await db.SaveChangesAsync(ct);
        return flag.Enabled;
    }

    public async Task SetGreeting(UserId userId, string text)
    {
        await greetings.SetAsync(userId, text);

        // Not EF, so say what changed, after the write.
        await cache.InvalidateAsync(Dashboards.Greeting.For(userId));
    }
}
