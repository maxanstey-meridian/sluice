using Playground.Manual.Application;

namespace Playground.Manual.Endpoints;

public static class ManualEndpoints
{
    public static void MapManualEndpoints(this WebApplication app, ManualPlaygroundRuntime runtime)
    {
        // Read through Sluice and remember the materialized value for the UI pane.
        app.MapPost(
            "/manual/api/dashboard/{user}",
            async (string user, CancellationToken ct) =>
            {
                var dash = await runtime.Cache.GetDashboard(user, ct);
                runtime.ReadState.Set(user, dash);
                return dash;
            }
        );

        // Shared write: invalidates every cached dashboard that observed dark_mode.
        app.MapPost(
            "/manual/api/flag/toggle",
            async (CancellationToken ct) => await runtime.Cache.ToggleDarkMode(ct)
        );

        // Selective write: invalidates only cached dashboards that observed this greeting.
        app.MapPost(
            "/manual/api/greeting/{user}",
            async (string user, UpdateGreetingRequest body, CancellationToken ct) =>
                await runtime.Cache.UpdateGreeting(user, body.Text, ct)
        );

        // Broad reset for demos and manual testing.
        app.MapPost(
            "/manual/api/flush",
            async (CancellationToken ct) => await runtime.Cache.FlushAll(ct)
        );

        app.MapGet(
            "/manual/sluice/events",
            (long? since) =>
                runtime
                    .EventSink.GetEventsSince(since ?? 0)
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
                        s.Event.AffectedEntryKeys,
                    })
        );

        // Stampede demo: flush, then fire 10 concurrent reads for the same user.
        // Only one becomes the leader and computes; the rest poll as followers.
        app.MapPost(
            "/manual/api/stampede/{user}",
            async (string user, CancellationToken ct) =>
            {
                await runtime.Cache.FlushAll(ct);
                var tasks = Enumerable
                    .Range(0, 10)
                    .Select(_ => runtime.Cache.GetDashboard(user, ct));
                var results = await Task.WhenAll(tasks);
                runtime.ReadState.Set(user, results[0]);
                return results[0];
            }
        );

        app.MapGet("/manual/sluice/state", () => runtime.ReadState.Snapshot());

        // Curated source files for the view-source panel.
        app.MapGet(
            "/manual/source",
            (IWebHostEnvironment env) =>
                new[]
                {
                    ReadSource(env, "Manual/Application/DashboardCache.cs", "DashboardCache.cs"),
                }
        );
    }

    private static SourceFile ReadSource(
        IWebHostEnvironment env,
        string relativePath,
        string displayName
    )
    {
        var fullPath = Path.Combine(env.ContentRootPath, relativePath);
        var code = File.Exists(fullPath)
            ? File.ReadAllText(fullPath)
            : $"// {relativePath} not found.";
        return new SourceFile(displayName, "csharp", code);
    }
}

public sealed record SourceFile(string Name, string Language, string Code);

// Minimal API request shape for the greeting mutation.
public sealed record UpdateGreetingRequest(string Text);
