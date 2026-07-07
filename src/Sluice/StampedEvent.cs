namespace Sluice;

public sealed record StampedEvent(long Seq, SluiceEvent Event);
