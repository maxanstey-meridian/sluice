using Microsoft.EntityFrameworkCore;
using Playground;
using Sluice;

var builder = WebApplication.CreateBuilder(args);

var connectionString = $"Data Source={Path.Combine(AppContext.BaseDirectory, "playground.db")}";

builder.Services.AddSluice();

// The factory also registers AppDb itself, scoped: DashboardActions' writes use it, and each compute takes its own
// from a scope of its own.
builder.Services.AddDbContextFactory<AppDb>(
    (services, options) => options.UseSqlite(connectionString).UseSluice(services)
);
builder.Services.AddSingleton<GreetingApi>();
builder.Services.AddSingleton<ComputeCounts>();
builder.Services.AddScoped<DashboardActions>();

var app = builder.Build();

// A fresh database on every run, so the demo always starts from the same state.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDb>();
    db.Database.EnsureDeleted();
    db.Database.EnsureCreated();
}

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet(
    "/api/dashboards/{userId}",
    (string userId, DashboardActions dashboards, CancellationToken ct) =>
        dashboards.GetDashboard(new UserId(userId), ct)
);
app.MapPost(
    "/api/flags/dark-mode/toggle",
    async (DashboardActions dashboards, CancellationToken ct) =>
        new { Enabled = await dashboards.ToggleDarkMode(ct) }
);
app.MapPut(
    "/api/greetings/{userId}",
    (string userId, GreetingRequest request, DashboardActions dashboards) =>
        dashboards.SetGreeting(new UserId(userId), request.Text)
);
app.MapPost("/api/cache/clear", (SluiceCache cache, CancellationToken ct) => cache.ClearAsync(ct));
app.MapGet(
    "/api/computes",
    (ComputeCounts computes) =>
        computes.Snapshot().ToDictionary(count => count.Key.Value, count => count.Value)
);

await app.RunAsync();

internal sealed record GreetingRequest(string Text);
