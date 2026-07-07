using Playground.Generated.Application;

namespace Playground.Generated.Endpoints;

public static class GeneratedEndpoints
{
    public static void MapGeneratedEndpoints(
        this WebApplication app,
        GeneratedPlaygroundRuntime runtime
    )
    {
        app.MapPost(
            "/generated/api/dashboard/{user}",
            async (string user, CancellationToken ct) =>
            {
                var dash = await runtime.Cache.GetDashboard(user, ct);
                runtime.ReadState.Set(user, dash);
                return dash;
            }
        );

        app.MapPost(
            "/generated/api/flag/toggle",
            async (CancellationToken ct) => await runtime.Cache.ToggleDarkMode(ct)
        );

        app.MapPost(
            "/generated/api/greeting/{user}",
            async (string user, UpdateGreetingRequest body, CancellationToken ct) =>
                await runtime.Cache.UpdateGreeting(user, body.Text, ct)
        );

        app.MapPost(
            "/generated/api/flush",
            async (CancellationToken ct) => await runtime.Cache.FlushAll(ct)
        );

        app.MapGet(
            "/generated/sluice/events",
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

        app.MapGet("/generated/sluice/state", () => runtime.ReadState.Snapshot());

        // Stampede demo: flush, then fire 10 concurrent reads for the same user.
        app.MapPost(
            "/generated/api/stampede/{user}",
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

        // Curated source files for the view-source panel.
        app.MapGet(
            "/generated/source",
            (IWebHostEnvironment env) =>
                new[]
                {
                    ReadSource(
                        env,
                        "Generated/Application/IGeneratedPlaygroundStore.cs",
                        "IGeneratedPlaygroundStore.cs"
                    ),
                    ReadSource(env, "Generated/Application/DashboardCache.cs", "DashboardCache.cs"),
                    FindGeneratedSource(
                        env,
                        "GeneratedDashboardSluice.g.cs",
                        "GeneratedDashboardSluice.g.cs"
                    ),
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

    private static SourceFile FindGeneratedSource(
        IWebHostEnvironment env,
        string filename,
        string displayName
    )
    {
        var objDir = Path.Combine(env.ContentRootPath, "obj");
        var match = Directory.Exists(objDir)
            ? Directory.GetFiles(objDir, filename, SearchOption.AllDirectories).FirstOrDefault()
            : null;
        var code = match is not null
            ? File.ReadAllText(match)
            : $"// {filename} not found. Build the project first.";
        return new SourceFile(displayName, "csharp", code);
    }
}

public sealed record UpdateGreetingRequest(string Text);

public sealed record SourceFile(string Name, string Language, string Code);
