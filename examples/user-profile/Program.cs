using Sluice;
using SluiceExample;

var store = new InMemoryUserStore();
var sluice = new SluiceKernel(new InMemoryCacheStore());
var queries = new UserQueries(store);
var commands = new UserCommands(sluice, store);

var alice = new UserId("alice");
var bob = new UserId("bob");

Console.WriteLine("=== Sluice User Profile Example ===");
Console.WriteLine();

// --- Simple query: cache miss then hit ---
Console.WriteLine("1. Get user Alice (cache miss)");
var user1 = await sluice.Get(queries.User, alice, CancellationToken.None);
Console.WriteLine($"   {user1.Name} <{user1.Email}>");
Console.WriteLine($"   Store calls: GetUser={store.GetUserCallCount}");
Console.WriteLine();

Console.WriteLine("2. Get user Alice again (cache hit)");
var user2 = await sluice.Get(queries.User, alice, CancellationToken.None);
Console.WriteLine($"   {user2.Name} <{user2.Email}>");
Console.WriteLine($"   Store calls: GetUser={store.GetUserCallCount} (unchanged)");
Console.WriteLine();

// --- Composite query: reads user + settings ---
Console.WriteLine("3. Get profile for Alice (cache miss, reads user + settings)");
var profile1 = await sluice.Get(queries.Profile, alice, CancellationToken.None);
Console.WriteLine($"   {profile1.Name}, darkMode={profile1.DarkMode}, lang={profile1.Language}");
Console.WriteLine($"   Store calls: GetUser={store.GetUserCallCount}, GetSettings={store.GetSettingsCallCount}");
Console.WriteLine();

Console.WriteLine("4. Get profile for Bob (different key, cache miss)");
var profile2 = await sluice.Get(queries.Profile, bob, CancellationToken.None);
Console.WriteLine($"   {profile2.Name}, darkMode={profile2.DarkMode}, lang={profile2.Language}");
Console.WriteLine();

// --- Dependency graph ---
Console.WriteLine("5. Dependency graph:");
Console.WriteLine();
Console.WriteLine(sluice.DumpGraph());

// --- Selective invalidation: change settings, not user ---
Console.WriteLine("6. Update Alice's dark mode to true");
await commands.UpdateDarkMode(alice, true, CancellationToken.None);
Console.WriteLine();

Console.WriteLine("7. Get profile for Alice (cache miss, settings changed)");
var profile3 = await sluice.Get(queries.Profile, alice, CancellationToken.None);
Console.WriteLine($"   {profile3.Name}, darkMode={profile3.DarkMode}, lang={profile3.Language}");
Console.WriteLine();

Console.WriteLine("8. Get user for Alice (STILL CACHED, user entity didn't change)");
var user3 = await sluice.Get(queries.User, alice, CancellationToken.None);
Console.WriteLine($"   {user3.Name} <{user3.Email}>");
Console.WriteLine($"   Store calls: GetUser={store.GetUserCallCount} (cache hit, no new store call)");
Console.WriteLine();

Console.WriteLine("=== Done ===");
Console.WriteLine();
Console.WriteLine("Updating dark mode evicted the profile (it reads userSettings),");
Console.WriteLine("but the user entry stayed cached (it never reads userSettings).");
Console.WriteLine("No manual cache key management. The dependency graph did it all.");
