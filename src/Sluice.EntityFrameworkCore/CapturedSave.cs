using System.Runtime.CompilerServices;
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
    /// <param name="context">The context about to save.</param>
    /// <param name="unfinished">
    /// A save on the same context that hasn't completed: the save this one runs inside (an interceptor that saves
    /// again), or one a concurrency conflict left behind. Its rows are carried into this save and published with
    /// it, since EF reports the end of the inner save before the outer one's, and the outer one then finds nothing.
    /// </param>
    internal static CapturedSave Capture(DbContext context, CapturedSave? unfinished)
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
        if (unfinished is not null)
        {
            save._changes.AddRange(unfinished._changes);
            save._originals.AddRange(unfinished._originals);
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
    /// also invalidates every foreign key that points at it, so database cascades evict <c>Children</c> readers.
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
            var source = ModelSources.WrittenEntity(entityType);
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
    // no original values from the database, so the parent it had is unknown. Two signs give it away: Update() marks
    // every property modified with its original value equal to its current one, and a stub's foreign key holds a
    // value the database can't (null in a required key, or the default of a non-nullable type, such as 0). A
    // nullable foreign key left null on a stub looks like a row with no parent, and can't be told apart.
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
                        ? property.ClrType.IsValueType
                            && Nullable.GetUnderlyingType(property.ClrType) is null
                            && original.Equals(
                                RuntimeHelpers.GetUninitializedObject(property.ClrType)
                            )
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
