namespace Sluice;

public sealed class NullEventSink : IEventSink
{
    public static readonly NullEventSink Instance = new();

    private NullEventSink() { }

    public void Emit(SluiceEvent evt) { }
}
