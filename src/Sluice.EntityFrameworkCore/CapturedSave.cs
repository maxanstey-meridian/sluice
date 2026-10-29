using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Sluice.EntityFrameworkCore;

/// <summary>
/// The rows one save writes, captured when EF initialises the save's first command: after every <c>SavingChanges</c>
/// interceptor, EF's change detection and its own cascades, before any row is written. Original foreign key and unique
/// column values are read then, because accepting the save overwrites them; keys and current values are read after it,
/// because generated keys are temporary until the save runs. A changed owned row counts as its owner modified. An
/// original value is what EF loaded, so it's only used where EF's <c>UPDATE</c> or <c>DELETE</c> checks the database
/// still holds it. A row stored in a table another entity type maps too (table splitting) also changes that type's row.
/// </summary>
internal sealed class CapturedSave
{
    // Each row, with the properties it writes when that's needed later, after EF has accepted the save: an update's
    // modified properties, for a type sharing its table with another.
    private readonly List<(
        EntityEntry Entry,
        EntityState State,
        IReadOnlyCollection<IProperty> Written
    )> _changes = [];
    private readonly List<Dependency> _originals = [];
    private readonly HashSet<IEntityType> _unversioned = [];

    // Whether the save started inside SluiceFlow.InvalidateNow, read on its own flow when it started.
    private readonly bool _now;

    private CapturedSave(ContextTransaction transaction, bool now)
    {
        Transaction = transaction;
        _now = now;
    }

    /// <summary>
    /// The transaction the save's first command ran in, with the ambient one its connection was opened in when the
    /// command couldn't see one. A save that completes reads its transaction again then, and falls back on the
    /// ambient one recorded here; a save left over when its context saves again uses this.
    /// </summary>
    internal ContextTransaction Transaction { get; }

    /// <summary>
    /// The types of the rows this save updates or deletes whose foreign keys or unique columns it invalidates whole,
    /// because they have no row version (<see cref="ModelSources.HasRowVersion"/>).
    /// </summary>
    internal IReadOnlyCollection<IEntityType> Unversioned => _unversioned;

    /// <summary>
    /// Captures what <paramref name="context"/> is saving, from its first command, without changing what EF saves.
    /// </summary>
    /// <param name="context">The context whose save EF is about to run the first command of.</param>
    /// <param name="transaction">The transaction that command runs in.</param>
    /// <param name="now">
    /// Whether the save started inside <see cref="SluiceFlow.InvalidateNow"/>, so everything it derives publishes hard.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// The model has a key, foreign key or unique column whose value converter isn't known to round-trip
    /// (<see cref="ModelSources.EnsureRoundTrips"/>).
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The core refuses one of the model's source names (<see cref="ModelSources.EnsureSources"/>).
    /// </exception>
    internal static CapturedSave Capture(
        DbContext context,
        ContextTransaction transaction,
        bool now
    )
    {
        // Before the save writes anything: once it has, a refusal could only be logged.
        ModelSources.EnsureSources(context);
        var save = new CapturedSave(transaction, now);
        var tracker = context.ChangeTracker;

        // EF has detected changes and applied its own cascades by now, and accepts none until every batch has run.
        // Detecting again would scan the tracker once more, and could only find an edit made since (by a command
        // interceptor), which this save doesn't write.
        var detect = tracker.AutoDetectChangesEnabled;
        List<EntityEntry> entries;
        tracker.AutoDetectChangesEnabled = false;
        try
        {
            entries = [.. tracker.Entries()];
        }
        finally
        {
            tracker.AutoDetectChangesEnabled = detect;
        }

        // An owned row is part of its owner, and readers only see it through the owner, so what it writes counts as
        // the owner's: an insert or delete every property, an update its modified ones. The tracker leaves the owner
        // unchanged when nothing of its own changed, so it's captured as modified then; a changed owner is captured
        // in its own right.
        var changed = new List<(EntityEntry Entry, EntityState State)>();
        var owned = new Dictionary<EntityEntry, List<IProperty>>();
        foreach (var entry in entries)
        {
            var state = StateOf(entry);
            if (state is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
            {
                continue;
            }

            if (!entry.Metadata.IsOwned())
            {
                changed.Add((entry, state));
                continue;
            }

            var owner = OwnerOf(entry, entries);
            if (!owned.TryGetValue(owner, out var written))
            {
                owned[owner] = written = [];
            }

            written.AddRange(
                Written(
                    entry.Properties,
                    entry.ComplexProperties,
                    all: state is not EntityState.Modified
                )
            );
        }

        foreach (var (entry, state) in changed)
        {
            save.Add(entry, state, owned.Remove(entry, out var written) ? written : []);
        }

        foreach (var (owner, written) in owned)
        {
            save.Add(owner, EntityState.Modified, written);
        }

        return save;
    }

    // The entry's own state. Before a save's first command, EF marks every unchanged entry that shares a row with a
    // changed one (table splitting, an owned type in its owner's table) Modified with nothing modified, so the row's
    // command carries it; what it writes is the other entry's, which is captured as itself. "Nothing modified" is EF's
    // own test for setting a Modified entry back to Unchanged: no property flagged, nor any complex collection, which
    // EntityEntry.ComplexProperties leaves out (a complex property's entry counts the collections nested in it).
    // Members isn't that test: its navigations report changes to other entries.
    private static EntityState StateOf(EntityEntry entry) =>
        entry.State is EntityState.Modified
        && !entry.Properties.Any(property => property.IsModified)
        && !entry.ComplexProperties.Any(property => property.IsModified)
        && !entry.ComplexCollections.Any(collection => collection.IsModified)
            ? EntityState.Unchanged
            : entry.State;

    /// <summary>
    /// Everything the save may have changed: each row's key, and its foreign keys and unique columns before and after,
    /// or whole when the value before is unknown. A deleted row also invalidates every foreign key that points at it,
    /// and the whole of every type the model lets the database delete or set to null with it
    /// (<see cref="ModelSources.Cascades"/>), since EF may not have loaded those rows. A save started inside
    /// <see cref="SluiceFlow.InvalidateNow"/> derives each of them hard, for every caller: the save's own publish, and
    /// a later save's publish of it as a leftover.
    /// </summary>
    internal List<Dependency> Dependencies() => Dependency.AsNow(Derived(), _now);

    private List<Dependency> Derived()
    {
        var dependencies = new List<Dependency>(_originals);
        foreach (var (entry, state, written) in _changes)
        {
            // A failed save's generated keys are temporary again, though an earlier batch may have committed the
            // row under one. Which row is unknown, so the whole type is invalidated, and so is the whole of each
            // foreign key still pointing at such a row.
            var entityType = entry.Metadata;
            var source = ModelSources.Entity(entityType);
            dependencies.Add(
                KeyOf(entry, entityType.FindPrimaryKey()!.Properties, Current) is { } key
                    ? source.For(key)
                    : source.All
            );

            foreach (var foreignKey in entityType.GetForeignKeys())
            {
                var foreignKeySource = ModelSources.ForeignKey(foreignKey);
                if (foreignKey.Properties.Any(property => entry.Property(property).IsTemporary))
                {
                    dependencies.Add(foreignKeySource.All);
                }
                else if (KeyOf(entry, foreignKey.Properties, Current) is { } principal)
                {
                    dependencies.Add(foreignKeySource.For(principal));
                }
            }

            // The value the row has now: an insert's, or what an update leaves. No reader reads by null.
            foreach (var property in ModelSources.UniqueColumns(entityType))
            {
                var uniqueSource = ModelSources.Unique(property);
                var value = entry.Property(property);
                if (value.IsTemporary)
                {
                    dependencies.Add(uniqueSource.All);
                }
                else if (ModelSources.StoredValue(property, value.CurrentValue) is { } current)
                {
                    dependencies.Add(uniqueSource.For(current));
                }
            }

            if (state is EntityState.Deleted)
            {
                // The key it was known by is the one EF loaded: a primary key can't change, an alternate one can.
                foreach (var foreignKey in entityType.GetReferencingForeignKeys())
                {
                    var foreignKeySource = ModelSources.ForeignKey(foreignKey);
                    if (!Checked(entityType, written: true, foreignKey.PrincipalKey.Properties))
                    {
                        dependencies.Add(foreignKeySource.All);
                    }
                    else if (
                        KeyOf(
                            entry,
                            foreignKey.PrincipalKey.Properties,
                            Current,
                            storedAs: foreignKey.Properties
                        ) is
                        { } principal
                    )
                    {
                        dependencies.Add(foreignKeySource.For(principal));
                    }
                }

                // A row deleted through its base type may be a derived one, whose foreign keys, and those pointing at
                // it, it can't key: it leaves its parents' lists under a derived type's foreign keys too, and its
                // value of a derived type's unique column.
                dependencies.AddRange(
                    entityType
                        .GetDerivedTypes()
                        .SelectMany(derived => derived.GetDeclaredReferencingForeignKeys())
                        .Concat(entityType.GetDerivedForeignKeys())
                        .Where(foreignKey => !foreignKey.IsOwnership && !foreignKey.IsBaseLinking())
                        .Select(foreignKey => ModelSources.ForeignKey(foreignKey).All)
                );
                dependencies.AddRange(
                    ModelSources
                        .DerivedUniqueColumns(entityType)
                        .Select(property => ModelSources.Unique(property).All)
                );

                dependencies.AddRange(ModelSources.Cascades(entityType));
            }

            foreach (var sharer in ModelSources.RowSharers(entityType))
            {
                dependencies.AddRange(SharerDependencies(entry, state, written, sharer));
            }
        }

        return dependencies;
    }

    // What a save of a row changes for another type stored in the same row (table splitting). A delete deletes the
    // row, so its row too: by key, or whole for a composite or unknown key. An insert or update changes it where the
    // save writes a column the other type maps: its row, and, whole, its foreign keys and unique columns in those
    // columns, whose value before is unknown, since that type's entry isn't loaded.
    private static IEnumerable<Dependency> SharerDependencies(
        EntityEntry entry,
        EntityState state,
        IReadOnlyCollection<IProperty> written,
        ModelSources.RowSharer sharer
    )
    {
        var key = sharer.OwnKey is { } ownKey
            ? KeyOf(entry, ownKey, Current, convertTo: sharer.Key)
            : null;
        if (state is EntityState.Deleted)
        {
            return key is not null && sharer.Key is [_]
                ? ModelSources.KeyedDeleteDependencies(sharer.Root, [key])
                : ModelSources.AllDependencies(sharer.Root, sharers: false);
        }

        bool Writes(IReadOnlyList<IProperty> properties) =>
            state is EntityState.Added || properties.Any(written.Contains);

        var source = ModelSources.Entity(sharer.Root);
        return
        [
            .. Writes(sharer.Shared)
                ? [key is null ? source.All : source.For(key)]
                : (Dependency[])[],
            .. sharer
                .Columns.Where(column => Writes(column.Writers))
                .Select(column => column.Whole),
        ];
    }

    // Owned is what the entry's owned rows write.
    private void Add(EntityEntry entry, EntityState state, IReadOnlyCollection<IProperty> owned)
    {
        _changes.Add(
            (
                entry,
                state,
                state is EntityState.Modified && ModelSources.RowSharers(entry.Metadata).Length > 0
                    ? [.. Written(entry.Properties, entry.ComplexProperties, all: false), .. owned]
                    : []
            )
        );
        if (state is EntityState.Added)
        {
            return;
        }

        // Whether EF writes the row itself: an owner saved for its owned row's sake has nothing of its own modified, so
        // EF checks none of its values.
        var entityType = entry.Metadata;
        var written = StateOf(entry) is EntityState.Modified or EntityState.Deleted;
        foreach (var foreignKey in entityType.GetForeignKeys())
        {
            var source = ModelSources.ForeignKey(foreignKey);
            if (!Checked(entityType, written, foreignKey.Properties))
            {
                _originals.Add(source.All);

                // An owner saved only for an owned row's sake evicts whole even with a row version, so there's
                // nothing to advise.
                if (written && !ModelSources.HasRowVersion(entityType))
                {
                    _unversioned.Add(entityType);
                }
            }
            else if (OriginalIsUnknown(entry, foreignKey.Properties, foreignKey.IsRequired))
            {
                _originals.Add(source.All);
            }
            else if (KeyOf(entry, foreignKey.Properties, Original) is { } key)
            {
                _originals.Add(source.For(key));
            }
        }

        // The value the row had, which its readers by that value read: a row that leaves it, by an update or a delete,
        // changes for them. Trusted as a foreign key's is, only where the database checks it.
        foreach (var property in ModelSources.UniqueColumns(entityType))
        {
            var source = ModelSources.Unique(property);
            if (!Checked(entityType, written, [property]))
            {
                _originals.Add(source.All);
                if (written && !ModelSources.HasRowVersion(entityType))
                {
                    _unversioned.Add(entityType);
                }
            }
            else if (OriginalIsUnknown(entry, [property], !property.IsNullable))
            {
                _originals.Add(source.All);
            }
            else if (
                ModelSources.StoredValue(property, entry.Property(property).OriginalValue) is
                { } original
            )
            {
                _originals.Add(source.For(original));
            }
        }
    }

    // The properties modified, or all of them, complex properties' included.
    private static IEnumerable<IProperty> Written(
        IEnumerable<PropertyEntry> properties,
        IEnumerable<ComplexPropertyEntry> complexProperties,
        bool all
    ) =>
        properties
            .Where(property => all || property.IsModified)
            .Select(property => property.Metadata)
            .Concat(
                complexProperties.SelectMany(complex =>
                    Written(complex.Properties, complex.ComplexProperties, all)
                )
            );

    // Whether the database holds the original values of the properties when a save that succeeds runs. A primary key
    // can't change. EF's UPDATE or DELETE of a row it writes, stored in one table, matches the originals of its
    // concurrency tokens, and with a row version the row is unchanged since it was loaded, or the save fails. Any other
    // value is what EF loaded, which another writer may have changed since, re-parenting the row without the save
    // noticing; so is a token of a row split across tables, which a write to another of its tables doesn't check.
    private static bool Checked(
        IEntityType entityType,
        bool written,
        IReadOnlyList<IProperty> properties
    ) =>
        properties.All(property =>
            property.IsPrimaryKey()
            || (written && property.IsConcurrencyToken && ModelSources.SingleTable(entityType))
        ) || (written && ModelSources.HasRowVersion(entityType));

    // A row saved without being loaded (attached, removed or updated from an object the context didn't query) has
    // no original values from the database, so the parent it had, or its unique value, is unknown. Two signs point to
    // one, and both can also match a loaded row, which only over-invalidates. Update(), or setting the state to
    // Modified, marks every property modified with its original value equal to its current one. A stub's foreign key
    // holds what the object was created with: null, which a required key can't hold, or the property's sentinel, EF's
    // "not set" value (the default of a non-nullable type, such as 0, unless HasSentinel says otherwise; null for a
    // nullable one), which a real parent rarely has. A stub created with any other value, or with a nullable key left
    // null, looks like a loaded row, and can't be told apart.
    private static bool OriginalIsUnknown(
        EntityEntry entry,
        IReadOnlyList<IProperty> properties,
        bool required
    ) =>
        properties.Any(property =>
        {
            var value = entry.Property(property);
            return (
                    value.IsModified
                    && property.GetValueComparer().Equals(value.OriginalValue, value.CurrentValue)
                )
                || (
                    value.OriginalValue is { } original
                        ? original.Equals(property.Sentinel)
                        : required
                );
        });

    // The non-owned entry an owned one belongs to, through any owned owners in between. An owned entry is only
    // tracked with its owner, which is found by the ownership's key.
    private static EntityEntry OwnerOf(EntityEntry owned, List<EntityEntry> entries)
    {
        var ownership = owned.Metadata.FindOwnership()!;
        var owner = entries.First(entry =>
            ownership.PrincipalEntityType.IsAssignableFrom(entry.Metadata)
            && ownership
                .PrincipalKey.Properties.Zip(ownership.Properties)
                .All(key =>
                    key.First.GetKeyValueComparer()
                        .Equals(
                            entry.Property(key.First).CurrentValue,
                            owned.Property(key.Second).CurrentValue
                        )
                )
        );
        return owner.Metadata.IsOwned() ? OwnerOf(owner, entries) : owner;
    }

    // A temporary value is unknown: it isn't what the database holds.
    private static object? Current(PropertyEntry property) =>
        property.IsTemporary ? null : property.CurrentValue;

    private static object? Original(PropertyEntry property) => property.OriginalValue;

    // The key a model source takes: the value itself, or an array of the values of a composite key.
    // Null when any part is null, or stored as null by its converter (a foreign key that points nowhere), or unknown.
    // A principal's key, read for its dependents' source, is stored as their foreign key stores it: storedAs. Another
    // type's key over the same columns is that type's value of what they store: convertTo.
    private static object? KeyOf(
        EntityEntry entry,
        IReadOnlyList<IProperty> properties,
        Func<PropertyEntry, object?> value,
        IReadOnlyList<IProperty>? storedAs = null,
        IReadOnlyList<IProperty>? convertTo = null
    )
    {
        var values = new object[properties.Count];
        for (var i = 0; i < values.Length; i++)
        {
            var part = ModelSources.StoredValue(
                (storedAs ?? properties)[i],
                value(entry.Property(properties[i]))
            );
            if (part is not null && convertTo is not null)
            {
                part = ModelSources.ValueAs(properties[i], convertTo[i], part);
            }

            if (part is null)
            {
                return null;
            }

            values[i] = part;
        }

        return values is [var single] ? single : values;
    }
}
