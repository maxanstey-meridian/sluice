using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Sluice.EntityFrameworkCore;

/// <summary>
/// The rows one save writes, captured before it runs. Original foreign key values are read then, because accepting
/// the save overwrites them; keys and current values are read after it, because generated keys are temporary until
/// the save runs. A changed owned row counts as its owner modified.
/// </summary>
internal sealed class CapturedSave
{
    private readonly List<(EntityEntry Entry, EntityState State)> _changes = [];
    private readonly List<Dependency> _originals = [];

    private CapturedSave(ContextTransaction transaction) => Transaction = transaction;

    /// <summary>
    /// The transaction the save began in. Only a save left over when its context saves again uses it; a save that
    /// completes reads its transaction then.
    /// </summary>
    internal ContextTransaction Transaction { get; }

    /// <summary>Captures what <paramref name="context"/> is about to save, without changing what EF saves.</summary>
    /// <exception cref="NotSupportedException">The context's provider isn't relational.</exception>
    internal static CapturedSave Capture(DbContext context)
    {
        // First, so a provider Sluice can't see transactions for is refused before anything else happens.
        var save = new CapturedSave(ContextTransaction.Of(context));
        var tracker = context.ChangeTracker;

        // EF cascades OnSaveChanges deletes after this point; surface them now. The public call forces every
        // cascade, so it's skipped when either timing is Never: it would delete what EF was told to leave.
        if (
            (
                tracker.CascadeDeleteTiming is CascadeTiming.OnSaveChanges
                || tracker.DeleteOrphansTiming is CascadeTiming.OnSaveChanges
            )
            && tracker.CascadeDeleteTiming is not CascadeTiming.Never
            && tracker.DeleteOrphansTiming is not CascadeTiming.Never
        )
        {
            tracker.CascadeChanges();
        }

        // Entries() detects changes only when AutoDetectChangesEnabled, exactly as SaveChanges is about to.
        var entries = tracker.Entries().ToList();
        foreach (var entry in entries)
        {
            if (
                entry.State
                is not (EntityState.Added or EntityState.Modified or EntityState.Deleted)
            )
            {
                continue;
            }

            if (!entry.Metadata.IsOwned())
            {
                save.Add(entry, entry.State);
            }
            // An owned row is part of its owner, and readers only see it through the owner, which the tracker leaves
            // Unchanged. A changed owner is captured in its own right.
            else if (OwnerOf(entry, entries) is { State: EntityState.Unchanged } owner)
            {
                save.Add(owner, EntityState.Modified);
            }
        }

        return save;
    }

    /// <summary>
    /// Everything the save may have changed: each row's key, and its foreign keys before and after. A deleted row
    /// also invalidates every foreign key that points at it, and the whole of every type the model lets the
    /// database delete or set to null with it (<see cref="ModelSources.Cascades"/>), since EF may not have loaded
    /// those rows.
    /// </summary>
    internal List<Dependency> Dependencies()
    {
        var dependencies = new List<Dependency>(_originals);
        foreach (var (entry, state) in _changes)
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

            if (state is EntityState.Deleted)
            {
                foreach (var foreignKey in entityType.GetReferencingForeignKeys())
                {
                    if (KeyOf(entry, foreignKey.PrincipalKey.Properties, Current) is { } principal)
                    {
                        dependencies.Add(ModelSources.ForeignKey(foreignKey).For(principal));
                    }
                }

                // A row deleted through its base type may be a derived one, whose own foreign keys it can't key.
                dependencies.AddRange(
                    entityType
                        .GetDerivedTypes()
                        .SelectMany(derived => derived.GetDeclaredReferencingForeignKeys())
                        .Select(foreignKey => ModelSources.ForeignKey(foreignKey).All)
                );

                dependencies.AddRange(ModelSources.Cascades(entityType));
            }
        }

        return dependencies;
    }

    private void Add(EntityEntry entry, EntityState state)
    {
        _changes.Add((entry, state));
        if (state is EntityState.Added)
        {
            return;
        }

        foreach (var foreignKey in entry.Metadata.GetForeignKeys())
        {
            var source = ModelSources.ForeignKey(foreignKey);
            if (OriginalIsUnknown(entry, foreignKey))
            {
                _originals.Add(source.All);
            }
            else if (KeyOf(entry, foreignKey.Properties, Original) is { } key)
            {
                _originals.Add(source.For(key));
            }
        }
    }

    // A row saved without being loaded (attached, removed or updated from an object the context didn't query) has
    // no original values from the database, so the parent it had is unknown. Two signs point to one, and both can
    // also match a loaded row, which only over-invalidates. Update(), or setting the state to Modified, marks every
    // property modified with its original value equal to its current one. A stub's foreign key holds what the object
    // was created with: null, which a required key can't hold, or the property's sentinel, EF's "not set" value (the
    // default of a non-nullable type, such as 0, unless HasSentinel says otherwise; null for a nullable one), which a
    // real parent rarely has. A stub created with any other value, or with a nullable key left null, looks like a
    // loaded row, and can't be told apart.
    private static bool OriginalIsUnknown(EntityEntry entry, IForeignKey foreignKey) =>
        foreignKey.Properties.Any(property =>
        {
            var value = entry.Property(property);
            return (
                    value.IsModified
                    && property.GetValueComparer().Equals(value.OriginalValue, value.CurrentValue)
                )
                || (
                    value.OriginalValue is { } original
                        ? original.Equals(property.Sentinel)
                        : foreignKey.IsRequired
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
    // Null when any part is null (a foreign key that points nowhere) or unknown.
    private static object? KeyOf(
        EntityEntry entry,
        IReadOnlyList<IProperty> properties,
        Func<PropertyEntry, object?> value
    )
    {
        var values = new object[properties.Count];
        for (var i = 0; i < values.Length; i++)
        {
            if (value(entry.Property(properties[i])) is not { } part)
            {
                return null;
            }

            values[i] = part;
        }

        return values is [var single] ? single : values;
    }
}
