using System.Reflection;

namespace Sluice;

/// <summary>
/// The source name guard, as <see cref="QueryNames"/> is for queries: a name keeps one key type and one key format.
/// Two declarations that disagree would give one key different text, so a write through one misses the readers of
/// the other. Nothing ever reads a declaration back from here.
/// </summary>
internal static class SourceNames
{
    private static readonly Lock _lock = new();
    private static readonly Dictionary<string, (Type Key, MethodInfo? Format)> _sources = new(
        StringComparer.Ordinal
    );

    /// <exception cref="ArgumentException">
    /// The name is already declared with another key type or another format.
    /// </exception>
    internal static void Claim(string name, Type key, Delegate format)
    {
        var method = FormatIdentity.Of(format);
        lock (_lock)
        {
            if (!_sources.TryGetValue(name, out var existing))
            {
                _sources[name] = (key, method);
                return;
            }

            if (existing.Key != key)
            {
                throw new ArgumentException(
                    $"Source '{name}' is already declared as Source<{existing.Key}>, so declaring it as Source<{key}> "
                        + "would give one key different text in each, and a write through one would miss the readers "
                        + "of the other. Give one of them a different name.",
                    nameof(name)
                );
            }

            // As for queries: a format that can't be compared is passed over.
            if (existing.Format is null || method is null)
            {
                _sources[name] = (key, existing.Format ?? method);
                return;
            }

            if (!FormatIdentity.Same(existing.Format, method))
            {
                throw new ArgumentException(
                    $"Source '{name}' is already declared as Source<{key}> with another format "
                        + $"({FormatIdentity.Describe(existing.Format)}), so one key could get different text in each, "
                        + "and a write through one would miss the readers of the other. Declare the source once and use "
                        + "that declaration everywhere.",
                    nameof(name)
                );
            }
        }
    }
}
