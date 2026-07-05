using Sluice;
using SluiceExample;

var store = new InMemoryUserStore();
var sluice = new SluiceKernel(new InMemoryCacheStore());
var users = new UserSluice(sluice, store);
var updateUser = new UpdateUserUseCase(users, store);

var alice = new UserId("alice");
var bob = new UserId("bob");

Console.WriteLine("=== Sluice User Profile Example ===");
Console.WriteLine();

// --- Simple query: cache miss then hit ---
Console.WriteLine("1. Get user Alice (cache miss)");
var user1 = await sluice.Get(users.UserById, alice, CancellationToken.None);
Console.WriteLine($"   {user1.Name} <{user1.Email}>");
Console.WriteLine($"   Store calls: GetUser={store.GetUserCallCount}");
Console.WriteLine();

Console.WriteLine("2. Get user Alice again (cache hit)");
var user2 = await sluice.Get(users.UserById, alice, CancellationToken.None);
Console.WriteLine($"   {user2.Name} <{user2.Email}>");
Console.WriteLine($"   Store calls: GetUser={store.GetUserCallCount} (unchanged)");
Console.WriteLine();

// --- Composite query: reads user + settings + preferences ---
Console.WriteLine("3. Get profile for Alice (cache miss, reads user + settings + preferences)");
var profile1 = await sluice.Get(users.Profile, alice, CancellationToken.None);
Console.WriteLine(
    $"   {profile1.Name}, darkMode={profile1.DarkMode}, lang={profile1.Language}, theme={profile1.Theme}"
);
Console.WriteLine(
    $"   Store calls: GetUser={store.GetUserCallCount}, GetSettings={store.GetSettingsCallCount}, GetPreferences={store.GetPreferencesCallCount}"
);
Console.WriteLine();

Console.WriteLine("4. Get profile for Bob (different key, cache miss)");
var profile2 = await sluice.Get(users.Profile, bob, CancellationToken.None);
Console.WriteLine(
    $"   {profile2.Name}, darkMode={profile2.DarkMode}, lang={profile2.Language}, theme={profile2.Theme}"
);
Console.WriteLine();

// --- Dependency graph ---
Console.WriteLine("5. Dependency graph:");
Console.WriteLine();
Console.WriteLine(sluice.DumpGraph());

// --- Use-case invalidation: one write changes multiple backing resources ---
Console.WriteLine("6. Update Alice (name, dark mode, preferences)");
await updateUser.Execute(
    alice,
    new UpdateUserInput("Alice Updated", true, "high-contrast"),
    CancellationToken.None
);
Console.WriteLine();

Console.WriteLine("7. Get profile for Alice (cache miss, one dependency changed)");
var profile3 = await sluice.Get(users.Profile, alice, CancellationToken.None);
Console.WriteLine(
    $"   {profile3.Name}, darkMode={profile3.DarkMode}, lang={profile3.Language}, theme={profile3.Theme}"
);
Console.WriteLine();

Console.WriteLine("8. Get user for Alice (cache miss, user entity changed)");
var user3 = await sluice.Get(users.UserById, alice, CancellationToken.None);
Console.WriteLine($"   {user3.Name} <{user3.Email}>");
Console.WriteLine(
    $"   Store calls: GetUser={store.GetUserCallCount} (incremented because entity:user:alice was invalidated)"
);
Console.WriteLine();

Console.WriteLine("=== Done ===");
Console.WriteLine();
Console.WriteLine("UpdateUserUseCase changed user, settings, and preferences.");
Console.WriteLine("Sluice evicted every cached entry that read any of those addresses.");
Console.WriteLine("No manual cache key management. The dependency graph did it all.");
