namespace Sluice;

/// <summary>
/// Marks an EF Core value converter that round-trips: converting any value the column can hold to .NET and back gives
/// that same stored value. Sluice refuses a model with a converter on a key, foreign key or unique column unless it
/// carries this, or is one of EF's built-ins that does (<c>EnumToNumberConverter</c> and the numeric
/// <c>CastingConverter</c>s into a type that holds every stored value, <c>DateTimeToTicksConverter</c>,
/// <c>TimeSpanToTicksConverter</c> and <c>GuidToBytesConverter</c>).
/// </summary>
/// <remarks>
/// It's your promise, and Sluice can't check it: it never sees the stored value, only what EF loaded. A save
/// invalidates by the value it would write back for what EF loaded, and a read by the value it asked for, so a
/// converter that loads one stored value and writes back another (<c>"01"</c> loaded as 1 and written as <c>"1"</c>, a
/// GUID stored with braces written without them, <c>" Active"</c> parsed as an enum) leaves the readers of what the
/// database held cached with stale data. A typed id over the column's own value (<c>id =&gt; id.Value</c>,
/// <c>value =&gt; new UserId(value)</c>) round-trips. A converter from <c>HasConversion(toProvider, fromProvider)</c>
/// has no class of its own: move the two conversions into a class deriving from <c>ValueConverter&lt;TModel,
/// TProvider&gt;</c> and mark that. To vouch for a converter you don't own, derive from it and mark the derived class.
/// The mark isn't inherited: a derived converter can convert differently.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class SluiceRoundTripsAttribute : Attribute;
