namespace Sluice;

public sealed record SluiceEvent(
    DateTimeOffset Timestamp,
    string Type,
    string? Operation = null,
    string? EntryKey = null,
    string? ResourceName = null,
    long? DurationMs = null,
    string? Detail = null
);
