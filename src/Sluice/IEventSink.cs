namespace Sluice;

public interface IEventSink
{
    public void Emit(SluiceEvent evt);

    public void EmitSafe(SluiceEvent evt)
    {
        try
        {
            Emit(evt);
        }
        catch { }
    }
}
