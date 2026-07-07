namespace Sluice.Example.Tests;

using Sluice;

public sealed class CapturingEventSink : IEventSink
{
    public List<SluiceEvent> Events { get; } = new();

    public void Emit(SluiceEvent evt) => Events.Add(evt);
}
