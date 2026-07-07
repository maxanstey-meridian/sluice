using Playground.Generated.Application;
using Playground.Generated.Endpoints;
using Playground.Manual.Application;
using Playground.Manual.Endpoints;

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

// Each mode is a fully isolated app slice: own store, Sluice kernel, event sink,
// cached query, and materialized UI state.
var manual = new ManualPlaygroundRuntime();
var generated = new GeneratedPlaygroundRuntime();

app.MapManualEndpoints(manual);
app.MapGeneratedEndpoints(generated);

app.MapFallbackToFile("index.html");

Console.WriteLine("Sluice Playground running at http://localhost:5300");
await app.RunAsync();
