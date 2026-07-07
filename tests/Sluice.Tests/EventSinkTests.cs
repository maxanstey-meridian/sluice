using System.Collections.Concurrent;

namespace Sluice.Tests;

public sealed class EventSinkTests
{
    public sealed class RingBufferSequence
    {
        [Fact]
        public void Emit_Assigns_Monotonically_Increasing_Seq()
        {
            var sink = new RingBufferEventSink(100);
            var now = DateTimeOffset.UtcNow;

            sink.Emit(new SluiceEvent(now, "hit", Operation: "op1", EntryKey: "k1"));
            sink.Emit(new SluiceEvent(now, "miss", Operation: "op1", EntryKey: "k2"));

            var events = sink.GetEventsSince(0);
            events.Should().HaveCount(2);
            events[0].Seq.Should().Be(1);
            events[1].Seq.Should().Be(2);
        }
    }

    public sealed class RingBufferCapacity
    {
        [Fact]
        public void Evicts_Oldest_When_Exceeding_Capacity()
        {
            var sink = new RingBufferEventSink(3);
            var now = DateTimeOffset.UtcNow;

            sink.Emit(new SluiceEvent(now, "hit", EntryKey: "k1"));
            sink.Emit(new SluiceEvent(now, "hit", EntryKey: "k2"));
            sink.Emit(new SluiceEvent(now, "hit", EntryKey: "k3"));
            sink.Emit(new SluiceEvent(now, "hit", EntryKey: "k4"));

            var events = sink.GetEventsSince(0);
            events.Should().HaveCount(3);
            events.Should().Contain(e => e.Seq == 2);
            events.Should().Contain(e => e.Seq == 3);
            events.Should().Contain(e => e.Seq == 4);
        }

        [Fact]
        public void Capacity_Of_One_Retains_Only_Latest()
        {
            var sink = new RingBufferEventSink(1);
            var now = DateTimeOffset.UtcNow;

            sink.Emit(new SluiceEvent(now, "hit", EntryKey: "k1"));
            sink.Emit(new SluiceEvent(now, "hit", EntryKey: "k2"));
            sink.Emit(new SluiceEvent(now, "hit", EntryKey: "k3"));

            var events = sink.GetEventsSince(0);
            events.Should().ContainSingle();
            events[0].Seq.Should().Be(3);
        }
    }

    public sealed class GetEventsSince
    {
        [Fact]
        public void Returns_Only_Events_With_Seq_Greater_Than_Given()
        {
            var sink = new RingBufferEventSink(100);
            var now = DateTimeOffset.UtcNow;

            sink.Emit(new SluiceEvent(now, "hit", EntryKey: "k1"));
            sink.Emit(new SluiceEvent(now, "hit", EntryKey: "k2"));
            sink.Emit(new SluiceEvent(now, "hit", EntryKey: "k3"));

            var events = sink.GetEventsSince(1);
            events.Should().HaveCount(2);
            events[0].Seq.Should().Be(2);
            events[1].Seq.Should().Be(3);
        }

        [Fact]
        public void Returns_Empty_When_No_Events_After_Seq()
        {
            var sink = new RingBufferEventSink(100);
            var now = DateTimeOffset.UtcNow;

            sink.Emit(new SluiceEvent(now, "hit", EntryKey: "k1"));

            var events = sink.GetEventsSince(1);
            events.Should().BeEmpty();
        }
    }

    public sealed class NullEventSink
    {
        [Fact]
        public void Emit_Does_Not_Throw()
        {
            var sink = Sluice.NullEventSink.Instance;
            var evt = new SluiceEvent(DateTimeOffset.UtcNow, "hit");
            Action act = () => sink.Emit(evt);
            act.Should().NotThrow();
        }

        [Fact]
        public void EmitSafe_Swallows_Exception_From_Throwing_Sink()
        {
            IEventSink sink = new ThrowingEventSink();
            var evt = new SluiceEvent(DateTimeOffset.UtcNow, "hit");
            Action act = () => sink.EmitSafe(evt);
            act.Should().NotThrow();
        }
    }

    public sealed class SafeEmitWrapper
    {
        [Fact]
        public async Task Throwing_Sink_Does_Not_Break_ExecuteAsync()
        {
            var throwingSink = new ThrowingEventSink();
            var store = new FakeStore();
            var customers = new TrackedCustomers(store);
            var orders = new TrackedOrders(store);
            var operation = new CustomerScoreOperation(customers, orders);
            var cacheStore = new InMemoryCacheStore();
            var registry = new OperationRegistry(cacheStore, eventSink: throwingSink);
            registry.Register(operation);

            var result = await registry.ExecuteAsync(
                operation,
                new CustomerId("c1"),
                CancellationToken.None
            );
            result.Score.Should().Be(90);
        }
    }

    public sealed class EmissionEvents
    {
        [Fact]
        public async Task ExecuteAsync_Emits_Hit_On_Cached_Return()
        {
            var sink = new CollectingEventSink();
            var store = new FakeStore();
            var customers = new TrackedCustomers(store);
            var orders = new TrackedOrders(store);
            var operation = new CustomerScoreOperation(customers, orders);
            var cacheStore = new InMemoryCacheStore();
            var registry = new OperationRegistry(cacheStore, eventSink: sink);
            registry.Register(operation);

            var customerId = new CustomerId("c1");

            await registry.ExecuteAsync(operation, customerId, CancellationToken.None);
            sink.Clear();

            await registry.ExecuteAsync(operation, customerId, CancellationToken.None);

            sink.Events.Should().ContainSingle(e => e.Event.Type == "hit");
            var hit = sink.Events.Single(e => e.Event.Type == "hit");
            hit.Event.Operation.Should().Be("customer.score");
            hit.Event.EntryKey.Should().Be("customer.score:v1:{\"customerId\":\"c1\"}");
        }

        [Fact]
        public async Task ExecuteAsync_Emits_Miss_On_Cache_Miss()
        {
            var sink = new CollectingEventSink();
            var store = new FakeStore();
            var customers = new TrackedCustomers(store);
            var orders = new TrackedOrders(store);
            var operation = new CustomerScoreOperation(customers, orders);
            var cacheStore = new InMemoryCacheStore();
            var registry = new OperationRegistry(cacheStore, eventSink: sink);
            registry.Register(operation);

            await registry.ExecuteAsync(operation, new CustomerId("c1"), CancellationToken.None);

            sink.Events.Should().Contain(e => e.Event.Type == "miss");
            var miss = sink.Events.First(e => e.Event.Type == "miss");
            miss.Event.Operation.Should().Be("customer.score");
            miss.Event.EntryKey.Should().Be("customer.score:v1:{\"customerId\":\"c1\"}");
        }

        [Fact]
        public async Task ExecuteAsync_Emits_Compute_With_Duration()
        {
            var sink = new CollectingEventSink();
            var store = new FakeStore();
            var customers = new TrackedCustomers(store);
            var orders = new TrackedOrders(store);
            var operation = new CustomerScoreOperation(customers, orders);
            var cacheStore = new InMemoryCacheStore();
            var registry = new OperationRegistry(cacheStore, eventSink: sink);
            registry.Register(operation);

            await registry.ExecuteAsync(operation, new CustomerId("c1"), CancellationToken.None);

            sink.Events.Should().Contain(e => e.Event.Type == "compute");
            var compute = sink.Events.First(e => e.Event.Type == "compute");
            compute.Event.Operation.Should().Be("customer.score");
            compute.Event.EntryKey.Should().Be("customer.score:v1:{\"customerId\":\"c1\"}");
            compute.Event.DurationMs.Should().NotBeNull();
            compute.Event.DurationMs.Should().BeGreaterOrEqualTo(0);
        }

        [Fact]
        public async Task ExecuteAsync_Emits_Miss_Leader_Compute_On_First_Call()
        {
            var sink = new CollectingEventSink();
            var store = new FakeStore();
            var customers = new TrackedCustomers(store);
            var orders = new TrackedOrders(store);
            var operation = new CustomerScoreOperation(customers, orders);
            var cacheStore = new InMemoryCacheStore();
            var registry = new OperationRegistry(cacheStore, eventSink: sink);
            registry.Register(operation);

            await registry.ExecuteAsync(operation, new CustomerId("c1"), CancellationToken.None);

            sink.Events.Should().Contain(e => e.Event.Type == "miss");
            sink.Events.Should().Contain(e => e.Event.Type == "stampede.leader");
            sink.Events.Should().Contain(e => e.Event.Type == "compute");
        }

        [Fact]
        public async Task ExecuteAsync_Emits_Stampede_Follower_When_Lease_Denied()
        {
            var sink = new CollectingEventSink();
            var store = new FakeStore();
            var gatedStore = new GateStore(store);
            var customers = new TrackedCustomers(gatedStore);
            var orders = new TrackedOrders(gatedStore);
            var operation = new CustomerScoreOperation(customers, orders);
            var cacheStore = new InMemoryCacheStore();
            var registry = new OperationRegistry(cacheStore, eventSink: sink);
            registry.Register(operation);

            var customerId = new CustomerId("c1");

            gatedStore.ArmGate();

            var leader = Task.Run(() =>
                registry.ExecuteAsync(operation, customerId, CancellationToken.None)
            );

            await gatedStore.ComputeEntered;
            await Task.Delay(100);

            sink.Clear();

            var followerTask = Task.Run(() =>
                registry.ExecuteAsync(operation, customerId, CancellationToken.None)
            );

            await Task.Delay(200);

            gatedStore.ReleaseGate();

            var follower = await followerTask;
            follower.Score.Should().Be(90);

            await leader;

            sink.Events.Should().Contain(e => e.Event.Type == "stampede.follower");
            sink.Events.Should().Contain(e => e.Event.Type == "hit");
        }

        [Fact]
        public async Task ExecuteAsync_Emits_Stampede_Timeout_When_Wait_Expires()
        {
            var sink = new CollectingEventSink();
            var store = new FakeStore();
            var gatedStore = new GateStore(store);
            var customers = new TrackedCustomers(gatedStore);
            var orders = new TrackedOrders(gatedStore);
            var operation = new CustomerScoreOperation(customers, orders);
            var cacheStore = new InMemoryCacheStore();
            var registry = new OperationRegistry(
                cacheStore,
                eventSink: sink,
                stampedeOptions: new StampedeOptions
                {
                    LeaseTtl = TimeSpan.FromSeconds(30),
                    WaitTimeout = TimeSpan.FromMilliseconds(50),
                    MaxBackoff = TimeSpan.FromMilliseconds(10),
                }
            );
            registry.Register(operation);

            var customerId = new CustomerId("c1");

            gatedStore.ArmGate();

            var leader = Task.Run(() =>
                registry.ExecuteAsync(operation, customerId, CancellationToken.None)
            );

            await gatedStore.ComputeEntered;
            await Task.Delay(100);

            sink.Clear();

            var followerTask = Task.Run(() =>
                registry.ExecuteAsync(operation, customerId, CancellationToken.None)
            );

            await Task.Delay(300);

            gatedStore.ReleaseGate();

            var follower = await followerTask;
            follower.Score.Should().Be(90);

            await leader;

            sink.Events.Should().Contain(e => e.Event.Type == "stampede.follower");
            sink.Events.Should().Contain(e => e.Event.Type == "stampede.timeout");
            sink.Events.Should().Contain(e => e.Event.Type == "compute");

            var followerEvent = sink.Events.First(e => e.Event.Type == "stampede.follower");
            var timeoutEvent = sink.Events.First(e => e.Event.Type == "stampede.timeout");

            followerEvent.Event.EntryKey.Should().Contain(customerId.Value);
            timeoutEvent.Event.EntryKey.Should().Contain(customerId.Value);
        }

        [Fact]
        public async Task ApplyAsync_Emits_Invalidate_Event()
        {
            var sink = new CollectingEventSink();
            var store = new FakeStore();
            var customers = new TrackedCustomers(store);
            var orders = new TrackedOrders(store);
            var operation = new CustomerScoreOperation(customers, orders);
            var cacheStore = new InMemoryCacheStore();
            var registry = new OperationRegistry(cacheStore, eventSink: sink);
            registry.Register(operation);

            await registry.ExecuteAsync(operation, new CustomerId("c1"), CancellationToken.None);
            sink.Clear();

            await registry.ApplyAsync(
                async ctx =>
                {
                    await ctx.Apply(
                        () =>
                            store.UpdateCustomer(
                                new CustomerId("c1"),
                                new CustomerPatch("NewName", "new@email.com")
                            ),
                        CustomerWriteEffects.Updated(new CustomerId("c1"))
                    );
                },
                CancellationToken.None
            );

            sink.Events.Should().Contain(e => e.Event.Type == "invalidate");
            var invalidate = sink.Events.First(e => e.Event.Type == "invalidate");
            invalidate.Event.Detail.Should().Contain("customer.score:v1:{\"customerId\":\"c1\"}");
            invalidate
                .Event.AffectedEntryKeys.Should()
                .Contain("customer.score:v1:{\"customerId\":\"c1\"}");
        }

        [Fact]
        public async Task FlushAllAsync_Emits_Flush_Event()
        {
            var sink = new CollectingEventSink();
            var cacheStore = new InMemoryCacheStore();
            var registry = new OperationRegistry(cacheStore, eventSink: sink);

            await registry.FlushAllAsync(CancellationToken.None);

            sink.Events.Should().ContainSingle(e => e.Event.Type == "flush");
        }

        [Fact]
        public async Task ExecuteAsync_Emits_Expire_When_Ttl_Expires()
        {
            var sink = new CollectingEventSink();
            var store = new FakeStore();
            var customers = new TrackedCustomers(store);
            var orders = new TrackedOrders(store);
            var operation = new CustomerScoreOperation(
                customers,
                orders,
                ttl: TimeSpan.FromMilliseconds(100)
            );
            var cacheStore = new InMemoryCacheStore();
            var registry = new OperationRegistry(cacheStore, eventSink: sink);
            registry.Register(operation);

            var customerId = new CustomerId("c1");

            await registry.ExecuteAsync(operation, customerId, CancellationToken.None);
            sink.Clear();

            await Task.Delay(200);

            await registry.ExecuteAsync(operation, customerId, CancellationToken.None);

            sink.Events.Should().Contain(e => e.Event.Type == "expire");
            var expire = sink.Events.First(e => e.Event.Type == "expire");
            expire.Event.Operation.Should().Be("customer.score");
            expire.Event.EntryKey.Should().Be("customer.score:v1:{\"customerId\":\"c1\"}");

            sink.Events.Should().Contain(e => e.Event.Type == "miss");
            sink.Events.Should().Contain(e => e.Event.Type == "compute");
        }
    }

    private sealed class CollectingEventSink : IEventSink
    {
        private readonly ConcurrentQueue<StampedEvent> _events = new();

        public IReadOnlyList<StampedEvent> Events
        {
            get { return _events.ToArray(); }
        }

        public void Emit(SluiceEvent evt)
        {
            _events.Enqueue(new StampedEvent(0, evt));
        }

        public void Clear()
        {
            while (_events.TryDequeue(out _)) { }
        }
    }

    private sealed class ThrowingEventSink : IEventSink
    {
        public void Emit(SluiceEvent evt) => throw new InvalidOperationException("sink fail");
    }

    private sealed class GateStore(IStore inner) : IStore
    {
        private TaskCompletionSource<bool>? _computeEntered;
        private TaskCompletionSource<bool>? _computeRelease;

        public Task ComputeEntered => _computeEntered?.Task ?? Task.CompletedTask;

        public void ArmGate()
        {
            _computeEntered = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            _computeRelease = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
        }

        public void ReleaseGate() => _computeRelease?.TrySetResult(true);

        public async Task<Customer> GetCustomer(CustomerId id)
        {
            _computeEntered?.TrySetResult(true);
            var release = _computeRelease;
            if (release is not null)
            {
                await release.Task;
            }
            return await inner.GetCustomer(id);
        }

        public Task<IReadOnlyList<Order>> GetOrdersByCustomer(CustomerId id) =>
            inner.GetOrdersByCustomer(id);

        public Task UpdateCustomer(CustomerId id, CustomerPatch patch) =>
            inner.UpdateCustomer(id, patch);

        public Task<Order> CreateOrder(CustomerId customerId, CreateOrderInput input) =>
            inner.CreateOrder(customerId, input);

        public Task<Order> GetOrder(OrderId orderId) => inner.GetOrder(orderId);

        public Task DeleteOrder(OrderId orderId) => inner.DeleteOrder(orderId);

        public Task ReassignOrder(OrderId orderId, CustomerId newCustomerId) =>
            inner.ReassignOrder(orderId, newCustomerId);
    }
}
