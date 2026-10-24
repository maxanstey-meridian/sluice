using System.Reflection;

namespace Sluice;

/// <summary>
/// The source name guard, as <see cref="QueryNames"/> is for queries: a name keeps one key type and one key format.
/// Two declarations that disagree would give one key different text, so a write through one misses the readers of
/// the other. A name a data model's package builds (the EF Core package's table names) belongs to the model, so no
/// application source can take it, whichever is declared first. Nothing ever reads a declaration back from here.
/// </summary>
internal static class SourceNames
{
    private static readonly Lock _lock = new();
    private static readonly Dictionary<string, (Type Key, MethodInfo? Format)> _sources = new(
        StringComparer.Ordinal
    );

    // Model sources aren't checked against each other: two models over one table share its names on purpose, each
    // building its own format.
    private static readonly HashSet<string> _modelNames = new(StringComparer.Ordinal);

    /// <exception cref="ArgumentException">
    /// The name is a model's, or already declared with another key type or another format.
    /// </exception>
    internal static void Claim(string name, Type key, Delegate format)
    {
        var method = FormatIdentity.Of(format);
        lock (_lock)
        {
            if (_modelNames.Contains(name))
            {
                throw ModelOwned(name);
            }

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

    /// <exception cref="ArgumentException">An application source already has the name.</exception>
    internal static void ClaimForModel(string name)
    {
        lock (_lock)
        {
            if (_sources.ContainsKey(name))
            {
                throw ModelOwned(name);
            }

            _modelNames.Add(name);
        }
    }

    private static ArgumentException ModelOwned(string name) =>
        new(
            $"Source '{name}' is declared by the application and is also a name the EF Core package gives the "
                + "dependencies it derives from a model (after a table, a foreign key's columns or a unique column), so "
                + "one row would get different key text in each, and a write through one would miss the readers of the "
                + "other. To invalidate those dependencies, use the EF Core package's set.Dependency(key) or "
                + "set.AllDependencies() on the DbSet; give an application source another name.",
            nameof(name)
        );
}
