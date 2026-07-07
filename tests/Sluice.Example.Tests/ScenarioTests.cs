namespace Sluice.Example.Tests;

using Sluice;
using UserProfile.Domain;
using UserProfile.Generated;
using UserProfile.Manual;

public sealed class ScenarioTests
{
    public enum ScenarioKind
    {
        Manual,
        Generated,
    }

    private static IScenarioRunner CreateScenario(ScenarioKind kind, IEventSink? sink = null) =>
        kind switch
        {
            ScenarioKind.Manual => new ManualScenario(sink),
            ScenarioKind.Generated => new GeneratedScenario(sink),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    public static IEnumerable<object[]> AllKinds =>
        new[] { new object[] { ScenarioKind.Manual }, new object[] { ScenarioKind.Generated } };

    [Theory]
    [MemberData(nameof(AllKinds))]
    public async Task FirstRead_IsMiss_ThenHit(ScenarioKind kind)
    {
        var scenario = CreateScenario(kind);
        var alice = new UserId("alice");

        await scenario.GetUser(alice, CancellationToken.None);
        scenario.Store.GetUserCallCount.Should().Be(1);

        await scenario.GetUser(alice, CancellationToken.None);
        scenario.Store.GetUserCallCount.Should().Be(1);
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public async Task CompositeQuery_ReadsAllDependencies(ScenarioKind kind)
    {
        var scenario = CreateScenario(kind);
        var alice = new UserId("alice");

        var profile = await scenario.GetProfile(alice, CancellationToken.None);

        profile.Name.Should().Be("Alice");
        scenario.Store.GetUserCallCount.Should().Be(1);
        scenario.Store.GetSettingsCallCount.Should().Be(1);
        scenario.Store.GetPreferencesCallCount.Should().Be(1);

        await scenario.GetProfile(alice, CancellationToken.None);
        scenario.Store.GetUserCallCount.Should().Be(1);
        scenario.Store.GetSettingsCallCount.Should().Be(1);
        scenario.Store.GetPreferencesCallCount.Should().Be(1);
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public async Task EntityWrite_InvalidatesDependentEntries(ScenarioKind kind)
    {
        var scenario = CreateScenario(kind);
        var alice = new UserId("alice");

        await scenario.GetProfile(alice, CancellationToken.None);
        scenario.Store.GetUserCallCount.Should().Be(1);

        await scenario.UpdateUser(
            alice,
            new UpdateUserInput("Alice v2", true, "dark"),
            CancellationToken.None
        );

        await scenario.GetProfile(alice, CancellationToken.None);
        scenario.Store.GetUserCallCount.Should().Be(2);
        scenario.Store.GetSettingsCallCount.Should().Be(2);
        scenario.Store.GetPreferencesCallCount.Should().Be(2);
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public async Task UnrelatedWrite_DoesNotInvalidate(ScenarioKind kind)
    {
        var scenario = CreateScenario(kind);
        var alice = new UserId("alice");
        var order1 = new OrderId("order-1");

        await scenario.GetUser(alice, CancellationToken.None);
        scenario.Store.GetUserCallCount.Should().Be(1);

        await scenario.UpdateOrder(order1, CancellationToken.None);

        await scenario.GetUser(alice, CancellationToken.None);
        scenario.Store.GetUserCallCount.Should().Be(1);
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public async Task CollectionRead_AndWrite(ScenarioKind kind)
    {
        var scenario = CreateScenario(kind);
        var admins = new GroupId("admins");

        await scenario.GetUsersByGroup(admins, CancellationToken.None);
        scenario.Store.GetUsersByGroupCallCount.Should().Be(1);

        await scenario.GetUsersByGroup(admins, CancellationToken.None);
        scenario.Store.GetUsersByGroupCallCount.Should().Be(1);

        await scenario.UpdateUsersByGroup(
            admins,
            new UpdateUserInput("Bulk Update", false, "system"),
            CancellationToken.None
        );

        await scenario.GetUsersByGroup(admins, CancellationToken.None);
        scenario.Store.GetUsersByGroupCallCount.Should().Be(2);
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public async Task ResultKey_CreateUser_InvalidatesByResult(ScenarioKind kind)
    {
        var scenario = CreateScenario(kind);
        var charlie = new UserId("charlie");

        await scenario.CreateUser(
            charlie,
            new CreateUserInput("Charlie", "charlie@example.com"),
            CancellationToken.None
        );
        scenario.Store.CreateUserCallCount.Should().Be(1);

        await scenario.GetUser(charlie, CancellationToken.None);
        scenario.Store.GetUserCallCount.Should().Be(1);

        await scenario.GetUser(charlie, CancellationToken.None);
        scenario.Store.GetUserCallCount.Should().Be(1);

        await scenario.CreateUser(
            charlie,
            new CreateUserInput("Charlie v2", "charlie2@example.com"),
            CancellationToken.None
        );

        await scenario.GetUser(charlie, CancellationToken.None);
        scenario.Store.GetUserCallCount.Should().Be(2);
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public async Task EscapeHatch_Invalidate_Works(ScenarioKind kind)
    {
        var scenario = CreateScenario(kind);
        var alice = new UserId("alice");

        await scenario.GetUser(alice, CancellationToken.None);
        scenario.Store.GetUserCallCount.Should().Be(1);

        await scenario.InvalidateUser(alice, CancellationToken.None);

        await scenario.GetUser(alice, CancellationToken.None);
        scenario.Store.GetUserCallCount.Should().Be(2);
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public async Task Stampede_ConcurrentAccess_ComputesOnce(ScenarioKind kind)
    {
        var scenario = CreateScenario(kind);
        var alice = new UserId("alice");

        var tasks = Enumerable
            .Range(0, 10)
            .Select(_ => scenario.GetUser(alice, CancellationToken.None));

        var results = await Task.WhenAll(tasks);

        results.Should().HaveCount(10);
        results.Should().AllBeEquivalentTo(results[0]);
        scenario.Store.GetUserCallCount.Should().Be(1);
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public async Task Events_Emitted_Correctly(ScenarioKind kind)
    {
        var sink = new CapturingEventSink();
        var scenario = CreateScenario(kind, sink);
        var alice = new UserId("alice");

        await scenario.GetUser(alice, CancellationToken.None);

        sink.Events.Should().Contain(e => e.Type == "miss");
        sink.Events.Should().Contain(e => e.Type == "compute");

        sink.Events.Clear();
        await scenario.GetUser(alice, CancellationToken.None);
        sink.Events.Should().Contain(e => e.Type == "hit");
        sink.Events.Should().NotContain(e => e.Type == "compute");

        sink.Events.Clear();
        await scenario.UpdateUser(
            alice,
            new UpdateUserInput("Alice v2", true, "dark"),
            CancellationToken.None
        );
        sink.Events.Should().Contain(e => e.Type == "invalidate");
    }
}
