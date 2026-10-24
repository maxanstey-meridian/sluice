using System.ComponentModel;

namespace Sluice.Infrastructure;

/// <summary>
/// Builds the sources a package derives from a data model, such as the EF Core package's entity types. For Sluice's
/// own packages; it isn't a supported API, and may change in any release.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class ModelSource
{
    /// <summary>
    /// A <see cref="Source{TKey}"/> whose name belongs to the model. It isn't checked against other model sources: a
    /// model's source names are unique by construction, and two models over the same tables share them on purpose,
    /// each building its own format. But no application source can have the name, whichever is declared first: an
    /// application's sources go through the <see cref="Source{TKey}"/> constructors, which then throw.
    /// </summary>
    /// <param name="name">The source name.</param>
    /// <param name="format">The key text, which must give different text for different keys.</param>
    /// <exception cref="ArgumentException">
    /// The name is blank, or an application source already has it.
    /// </exception>
    public static Source<TKey> Create<TKey>(string name, Func<TKey, string> format)
        where TKey : notnull => Source<TKey>.ForModel(name, format);
}
