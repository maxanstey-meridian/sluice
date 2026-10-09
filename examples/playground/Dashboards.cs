using Sluice;

namespace Playground;

public sealed record Dashboard(string Name, bool DarkMode, string? Greeting);

public static class Dashboards
{
    // Something a cached value can depend on: one user's greeting.
    public static readonly Source<UserId> Greeting = new("greeting", id => id.Value);

    // Something cached: a user's dashboard, kept for five minutes unless invalidated.
    public static readonly Query<UserId, Dashboard> ForUser = new("dashboard", id => id.Value)
    {
        Duration = TimeSpan.FromMinutes(5),
    };
}
