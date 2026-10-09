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

    private CapturedSave() { }

    /// <summary>Captures what <paramref name="context"/> is about to save, without changing what EF saves.</summary>
    internal static CapturedSave Capture(DbContext context)
    {
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

        var save = new CapturedSave();

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
    /// also invalidates every foreign key that points at it, so database cascades evict <c>Children</c> readers.
    /// A foreign key value still temporary (pointing at a row a failed save inserted) is skipped.
    /// </summary>
    internal List<Dependency> Dependencies()
    {
        var dependencies = new List<Dependency>(_originals);
        foreach (var (entry, state) in _changes)
        {
            // A failed save's generated key is temporary again, though an earlier batch may have committed the
            // row under it. Which row is unknown, so the whole type is invalidated.
            var entityType = entry.Metadata;
            var source = ModelSources.WrittenEntity(entityType);
            dependencies.Add(
                KeyOf(entry, entityType.FindPrimaryKey()!.Properties, Current) is { } key
                    ? source.For(key)
                    : source.All
            );

            dependencies.AddRange(ForeignKeys(entry, Current));

            if (state is EntityState.Deleted)
            {
                foreach (var foreignKey in entityType.GetReferencingForeignKeys())
                {
                    if (KeyOf(entry, foreignKey.PrincipalKey.Properties, Current) is { } principal)
                    {
                        dependencies.Add(ModelSources.ForeignKey(foreignKey).For(principal));
                    }
                }
            }
        }

        return dependencies;
    }

    private void Add(EntityEntry entry, EntityState state)
    {
        _changes.Add((entry, state));
        if (state is not EntityState.Added)
        {
            _originals.AddRange(ForeignKeys(entry, property => property.OriginalValue));
        }
    }

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

    private static object? Current(PropertyEntry property) =>
        property.IsTemporary ? null : property.CurrentValue;

    private static IEnumerable<Dependency> ForeignKeys(
        EntityEntry entry,
        Func<PropertyEntry, object?> value
    )
    {
        foreach (var foreignKey in entry.Metadata.GetForeignKeys())
        {
            if (KeyOf(entry, foreignKey.Properties, value) is { } key)
            {
                yield return ModelSources.ForeignKey(foreignKey).For(key);
            }
        }
    }

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
