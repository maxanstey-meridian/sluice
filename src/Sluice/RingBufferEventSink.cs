using System.Collections.Concurrent;

namespace Sluice;

public sealed class RingBufferEventSink : IEventSink
{
    private readonly ConcurrentQueue<StampedEvent> _buffer = new();
    private readonly int _capacity;
    private long _seq;

    public RingBufferEventSink(int capacity = 1000)
    {
        _capacity = Math.Max(1, capacity);
    }

    public void Emit(SluiceEvent evt)
    {
        var seq = Interlocked.Increment(ref _seq);
        _buffer.Enqueue(new StampedEvent(seq, evt));
        while (_buffer.Count > _capacity)
        {
            _buffer.TryDequeue(out _);
        }
    }

    public List<StampedEvent> GetEventsSince(long since)
    {
        return _buffer.Where(e => e.Seq > since).ToList();
    }
}
