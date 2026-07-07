var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://localhost:5200");

var app = builder.Build();
app.UseStaticFiles();
app.MapFallbackToFile("index.html");

Console.WriteLine("Sluice Dashboard running at http://localhost:5200");
await app.RunAsync();
