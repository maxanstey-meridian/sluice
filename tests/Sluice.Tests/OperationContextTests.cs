namespace Sluice.Tests;

public sealed class OperationContextTests
{
    [Fact]
    public void ObservedReads_Starts_Empty()
    {
        var ctx = new OperationContext(CancellationToken.None);

        ctx.ObservedReads.Should().BeEmpty();
    }

    [Fact]
    public void RecordRead_Adds_In_Order()
    {
        var ctx = new OperationContext(CancellationToken.None);

        ctx.RecordRead(new ResourceAddress(ResourceKind.Entity, "a", "1"));
        ctx.RecordRead(new ResourceAddress(ResourceKind.Entity, "b", "2"));
        ctx.RecordRead(new ResourceAddress(ResourceKind.Entity, "c", "3"));

        ctx.ObservedReads.Should().HaveCount(3);
        ctx.ObservedReads[0].Should().Be(new ResourceAddress(ResourceKind.Entity, "a", "1"));
        ctx.ObservedReads[1].Should().Be(new ResourceAddress(ResourceKind.Entity, "b", "2"));
        ctx.ObservedReads[2].Should().Be(new ResourceAddress(ResourceKind.Entity, "c", "3"));
    }

    [Fact]
    public void Clock_Defaults_To_System()
    {
        var ctx = new OperationContext(CancellationToken.None);

        ctx.Clock.Should().Be(TimeProvider.System);
    }

    [Fact]
    public void Clock_Is_Injectable()
    {
        var fakeClock = new FakeTimeProvider();
        var ctx = new OperationContext(fakeClock, CancellationToken.None);

        ctx.Clock.Should().Be(fakeClock);
    }

    [Fact]
    public void CancellationToken_Flows_Through_Constructor()
    {
        var ct = new CancellationToken(canceled: true);
        var ctx = new OperationContext(ct);

        ctx.CancellationToken.Should().Be(ct);
        ctx.CancellationToken.IsCancellationRequested.Should().BeTrue();
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public FakeTimeProvider(DateTimeOffset? now = null)
        {
            _now = now ?? DateTimeOffset.UtcNow;
        }

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
