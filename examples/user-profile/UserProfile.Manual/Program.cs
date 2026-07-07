using UserProfile.Domain;
using UserProfile.Manual;

var scenario = new ManualScenario();
var alice = new UserId("alice");
var admins = new GroupId("admins");

Console.WriteLine("=== Sluice Manual Example (Tier 1) ===");
Console.WriteLine();

Console.WriteLine("1. Get user Alice (cache miss)");
var user = await scenario.GetUser(alice, CancellationToken.None);
Console.WriteLine($"   {user.Name} <{user.Email}>");
Console.WriteLine($"   Store calls: GetUser={scenario.Store.GetUserCallCount}");
Console.WriteLine();

Console.WriteLine("2. Get user Alice again (cache hit)");
await scenario.GetUser(alice, CancellationToken.None);
Console.WriteLine($"   Store calls: GetUser={scenario.Store.GetUserCallCount} (unchanged)");
Console.WriteLine();

Console.WriteLine("3. Get profile for Alice (composite query)");
var profile = await scenario.GetProfile(alice, CancellationToken.None);
Console.WriteLine(
    $"   {profile.Name}, darkMode={profile.DarkMode}, lang={profile.Language}, theme={profile.Theme}"
);
Console.WriteLine();

Console.WriteLine("4. Get users by group 'admins'");
var groupUsers = await scenario.GetUsersByGroup(admins, CancellationToken.None);
Console.WriteLine($"   {string.Join(", ", groupUsers.Select(u => u.Name))}");
Console.WriteLine();

Console.WriteLine("5. Update Alice (invalidates user + settings + preferences)");
await scenario.UpdateUser(
    alice,
    new UpdateUserInput("Alice v2", true, "dark"),
    CancellationToken.None
);
Console.WriteLine();

Console.WriteLine("6. Get profile for Alice (cache miss — all dependencies invalidated)");
var profile2 = await scenario.GetProfile(alice, CancellationToken.None);
Console.WriteLine($"   {profile2.Name}, darkMode={profile2.DarkMode}, theme={profile2.Theme}");
Console.WriteLine();

Console.WriteLine("7. Create user Charlie (ResultKey invalidation)");
var charlie = new UserId("charlie");
await scenario.CreateUser(
    charlie,
    new CreateUserInput("Charlie", "charlie@example.com"),
    CancellationToken.None
);
var charlieUser = await scenario.GetUser(charlie, CancellationToken.None);
Console.WriteLine($"   {charlieUser.Name} <{charlieUser.Email}>");
Console.WriteLine();

Console.WriteLine("=== Done ===");
