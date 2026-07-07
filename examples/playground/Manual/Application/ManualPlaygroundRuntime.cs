using Sluice;

namespace Playground.Manual.Application;

public sealed class ManualPlaygroundRuntime
{
    public ManualPlaygroundRuntime()
    {
        EventSink = new RingBufferEventSink(1000);
        Store = new PlaygroundStore();
        Sluice = new SluiceKernel(new InMemoryCacheStore(), eventSink: EventSink);
        Cache = new DashboardCache(Sluice, Store);
        ReadState = new DashboardReadState();
    }

    public RingBufferEventSink EventSink { get; }

    public PlaygroundStore Store { get; }

    public SluiceKernel Sluice { get; }

    public DashboardCache Cache { get; }

    public DashboardReadState ReadState { get; }
}
