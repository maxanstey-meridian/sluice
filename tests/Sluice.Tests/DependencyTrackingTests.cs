using Sluice.Tests.Support;

namespace Sluice.Tests;

public sealed class DependencyTrackingTests
{
    private static readonly Source<string> _users = new("tracking-users");
    private static readonly Source<string> _flags = new("tracking-flags");
    private static readonly Source<string> _greetings = new("tracking-greetings");
    private static readonly Source<int> _invoices = new("tracking-invoices");
    private static readonly Source<int> _orders = new("tracking-orders");
    private static readonly Query<string, string> _dashboard = new("tracking-dashboard");
    private static readonly Query<string, string> _invoiceSummary = new("tracking-invoice-summary");
    private static readonly Query<string, string> _orderSummary = new("tracking-order-summary");

    private readonly Dictionary<string, string> _roles = new()
    {
        ["alice"] = "admin",
        ["bob"] = "member",
    };
    private readonly Dictionary<string, string> _greetingRows = new()
    {
        ["alice"] = "hi alice",
        ["bob"] = "hi bob",
    };
    private int _dashboardRuns;

    private Task<string> Dashboard(SluiceCache cache, string user) =>
        cache
            .GetOrSetAsync(
                _dashboard,
                user,
                (read, _) =>
                {
                    _dashboardRuns++;
                    read.From(_users.For(user), _flags.For("dark-mode"));
                    var role = _roles[user];

                    // Only admins read their greeting, so only admins depend on it.
                    string? greeting = null;
                    if (role == "admin")
                    {
                        read.From(_greetings.For(user));
                        greeting = _greetingRows[user];
                    }

                    return Task.FromResult($"{user}:{role}:{greeting}");
                }
            )
            .AsTask();

    [Fact]
    public async Task A_conditional_read_creates_a_dependency_only_when_it_runs()
    {
        await using var node = new SluiceNode();
        await Dashboard(node.Cache, "alice");
        await Dashboard(node.Cache, "bob");

        _greetingRows["bob"] = "hello bob";
        await node.Cache.InvalidateAsync(_greetings.For("bob"));
        await Dashboard(node.Cache, "bob");
        _dashboardRuns.Should().Be(2, "Bob never read his greeting");

        _greetingRows["alice"] = "hello alice";
        await node.Cache.InvalidateAsync(_greetings.For("alice"));
        (await Dashboard(node.Cache, "alice")).Should().Be("alice:admin:hello alice");
        _dashboardRuns.Should().Be(3);
    }

    [Fact]
    public async Task Invalidating_the_flag_evicts_both_dashboards()
    {
        await using var node = new SluiceNode();
        await Dashboard(node.Cache, "alice");
        await Dashboard(node.Cache, "bob");

        await node.Cache.InvalidateAsync(_flags.For("dark-mode"));
        await Dashboard(node.Cache, "alice");
        await Dashboard(node.Cache, "bob");

        _dashboardRuns.Should().Be(4);
    }

    [Fact]
    public async Task Invalidating_a_dependency_leaves_entries_that_did_not_read_it_cached()
    {
        await using var node = new SluiceNode();
        var invoices = new CountingReader(node.Cache, _invoiceSummary);
        var orders = new CountingReader(node.Cache, _orderSummary);
        await invoices.GetAsync("c1", _invoices.For(1));
        await orders.GetAsync("c1", _orders.For(1));

        await node.Cache.InvalidateAsync(_invoices.For(1));
        await invoices.GetAsync("c1", _invoices.For(1));
        await orders.GetAsync("c1", _orders.For(1));

        invoices.Runs("c1").Should().Be(2);
        orders.Runs("c1").Should().Be(1);
    }

    [Fact]
    public async Task A_recompute_that_stops_reading_a_dependency_is_no_longer_evicted_by_it()
    {
        await using var node = new SluiceNode();
        await Dashboard(node.Cache, "alice");

        // Alice's role and greeting change together. Triggering the recompute through the greeting keeps the
        // new entry free of every tag invalidated so far, so the last step can't pass or fail by clock tick.
        _roles["alice"] = "member";
        await node.Cache.InvalidateAsync(_greetings.For("alice"));
        (await Dashboard(node.Cache, "alice")).Should().Be("alice:member:");

        await node.Cache.InvalidateAsync(_greetings.For("alice"));
        await Dashboard(node.Cache, "alice");

        _dashboardRuns.Should().Be(2);
    }

    [Fact]
    public async Task Reading_two_dependencies_in_one_call_records_both()
    {
        void Record(SluiceReads read) => read.From(_invoices.For(1), _orders.For(2));

        (await Eviction.IsEvictedBy(Record, _invoices.For(1))).Should().BeTrue();
        (await Eviction.IsEvictedBy(Record, _orders.For(2))).Should().BeTrue();
        (await Eviction.IsEvictedBy(Record, _orders.For(3))).Should().BeFalse();
    }

    [Fact]
    public async Task Reads_from_concurrent_tasks_inside_one_compute_are_all_recorded()
    {
        var ids = Enumerable.Range(0, 32).ToArray();

        async Task Record(SluiceReads read)
        {
            var start = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            var helpers = ids.Select(id =>
                    Task.Run(async () =>
                    {
                        await start.Task;
                        read.From(_invoices.For(id));
                    })
                )
                .ToArray();
            start.SetResult();
            await Task.WhenAll(helpers);
        }

        foreach (var id in ids)
        {
            (await Eviction.IsEvictedBy(Record, _invoices.For(id)))
                .Should()
                .BeTrue($"invoice {id} was read");
        }
    }

    [Fact]
    public async Task Recording_a_read_after_the_compute_returned_throws()
    {
        await using var node = new SluiceNode();
        SluiceReads? escaped = null;
        await node.Cache.GetOrSetAsync(
            _invoiceSummary,
            "late",
            (read, _) =>
            {
                escaped = read;
                read.From(_invoices.For(1));
                return Task.FromResult("value");
            }
        );

        var fetched = false;
        var late = () => escaped!.From(_invoices.For(2));
        Action lateFused = () =>
            escaped!.From(
                _invoices.For(3),
                _ =>
                {
                    fetched = true;
                    return Task.FromResult("fetched");
                }
            );

        late.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*after the compute returned*");
        lateFused
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*after the compute returned*");
        fetched.Should().BeFalse("the fetch must not run once its read can't be recorded");
    }

    [Fact]
    public async Task A_fused_read_returns_the_fetch_result_with_the_compute_token_and_records_the_dependency()
    {
        await using var node = new SluiceNode();
        using var caller = new CancellationTokenSource();
        CancellationToken computeToken = default;
        CancellationToken fetchToken = default;

        var value = await node.Cache.GetOrSetAsync(
            _invoiceSummary,
            "fused",
            (read, ct) =>
            {
                computeToken = ct;
                return read.From(
                    _invoices.For(1),
                    ct =>
                    {
                        fetchToken = ct;
                        return Task.FromResult("invoice 1");
                    }
                );
            },
            caller.Token
        );

        value.Should().Be("invoice 1");
        fetchToken.Should().Be(computeToken);
        fetchToken.Should().Be(caller.Token);
        (
            await Eviction.IsEvictedBy(
                read => read.From(_invoices.For(1), _ => Task.FromResult(1)),
                _invoices.For(1)
            )
        )
            .Should()
            .BeTrue();
        (
            await Eviction.IsEvictedBy(
                read => read.From(_invoices.For(1), _ => Task.FromResult(1)),
                _invoices.For(2)
            )
        )
            .Should()
            .BeFalse();
    }

    [Fact]
    public async Task A_fused_read_of_several_dependencies_records_them_all()
    {
        Task Record(SluiceReads read) =>
            read.From([_invoices.For(1), _orders.For(2)], _ => Task.FromResult(1));

        (await Eviction.IsEvictedBy(Record, _invoices.For(1))).Should().BeTrue();
        (await Eviction.IsEvictedBy(Record, _orders.For(2))).Should().BeTrue();
        (await Eviction.IsEvictedBy(Record, _orders.For(3))).Should().BeFalse();
    }

    [Fact]
    public async Task A_null_dependency_in_a_read_throws_before_the_fetch_runs()
    {
        await using var node = new SluiceNode();
        var fetched = false;

        Task<string> Fetch(CancellationToken ct)
        {
            fetched = true;
            return Task.FromResult("fetched");
        }

        Task<string> Get(string key, Func<SluiceReads, Task<string>> compute) =>
            node.Cache.GetOrSetAsync(_invoiceSummary, key, (read, _) => compute(read)).AsTask();

        var single = () => Get("single", read => read.From((Dependency)null!, Fetch));
        var inList = () => Get("list", read => read.From([_invoices.For(1), null!], Fetch));

        await single.Should().ThrowExactlyAsync<ArgumentNullException>();
        await inList.Should().ThrowExactlyAsync<ArgumentException>().WithMessage("*null*");
        fetched.Should().BeFalse();
    }

    [Fact]
    public async Task A_fallback_for_a_failed_fused_fetch_is_evicted_by_its_dependency()
    {
        await using var node = new SluiceNode();
        var runs = 0;

        // The greeting service is down, so the dashboard shows no greeting; the user read succeeded.
        Task<string> GetDashboard() =>
            node
                .Cache.GetOrSetAsync(
                    _dashboard,
                    "alice",
                    async (read, _) =>
                    {
                        runs++;
                        read.From(_users.For("alice"));
                        try
                        {
                            return await read.From(
                                _greetings.For("alice"),
                                _ =>
                                    Task.FromException<string>(
                                        new HttpRequestException("greeting service down")
                                    )
                            );
                        }
                        catch (HttpRequestException)
                        {
                            return "no greeting";
                        }
                    }
                )
                .AsTask();

        (await GetDashboard()).Should().Be("no greeting");
        await node.Cache.InvalidateAsync(_greetings.For("alice"));
        await GetDashboard();

        runs.Should().Be(2);
    }

    [Fact]
    public async Task A_failed_fused_fetch_that_escapes_reaches_the_caller_unchanged_and_caches_nothing()
    {
        await using var node = new SluiceNode();
        var failure = new HttpRequestException("greeting service down");
        var runs = 0;

        var failing = () =>
            node
                .Cache.GetOrSetAsync(
                    _dashboard,
                    "bob",
                    (read, _) =>
                    {
                        runs++;
                        return read.From(
                            _greetings.For("bob"),
                            _ => Task.FromException<string>(failure)
                        );
                    }
                )
                .AsTask();

        (await failing.Should().ThrowAsync<HttpRequestException>())
            .Which.Should()
            .BeSameAs(failure);
        (
            await node.Cache.GetOrSetAsync(
                _dashboard,
                "bob",
                (read, _) =>
                {
                    runs++;
                    return read.From(_greetings.For("bob"), _ => Task.FromResult("hi bob"));
                }
            )
        ).Should().Be("hi bob");
        runs.Should().Be(2);
    }

    [Fact]
    public async Task From_accepts_a_list_and_a_projection_and_records_every_item()
    {
        List<Dependency> list = [_invoices.For(1), _invoices.For(2)];
        int[] orderIds = [3, 4];

        void Record(SluiceReads read)
        {
            read.From(list);
            read.From(orderIds.Select(_orders.For));
        }

        (await Eviction.IsEvictedBy(Record, _invoices.For(1))).Should().BeTrue();
        (await Eviction.IsEvictedBy(Record, _invoices.For(2))).Should().BeTrue();
        (await Eviction.IsEvictedBy(Record, _orders.For(3))).Should().BeTrue();
        (await Eviction.IsEvictedBy(Record, _orders.For(4))).Should().BeTrue();
        (await Eviction.IsEvictedBy(Record, _orders.For(5))).Should().BeFalse();
    }
}
