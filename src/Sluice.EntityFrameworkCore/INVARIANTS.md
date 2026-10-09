# Invariants

Rules a reasonable-looking change could break. Each names the one place that enforces it and the test that fails if it stops. Test names are in `tests/Sluice.EntityFrameworkCore.Tests`. The core's rules (`SLUICE-CORE-`) are in `src/Sluice/INVARIANTS.md`.

## SLUICE-EF-001 EF reads never track

**Rule:** `read.Entity`, `read.Children` and `read.All` return `AsNoTracking()` queries.
**Why:** a tracked query hands back the context's existing instance, so the cache would store the caller's unsaved edits, or a stale identity-resolved value, for every caller.
**Accepts:** cached entities are detached; callers who want to edit one load it again on their own context.
**Owner:** `SluiceReadsExtensions.WhereEquals` and `SluiceReadsExtensions.All`.
**Proof:** `EntityReadTests.Reads_do_not_track_so_an_unsaved_edit_on_the_callers_context_is_not_cached`.

## SLUICE-EF-002 EF reads refuse to run inside a transaction

**Rule:** `read.Entity`, `read.Children` and `read.All` throw `InvalidOperationException` when the context's connection has a current EF transaction, an enlisted transaction or an ambient one: the inputs EF's batch executor checks, so a `TransactionScope` over a connection with `Enlist=false` doesn't count, as it doesn't for EF.
**Why:** a compute inside a transaction caches rows other callers can't see yet (and that may roll back), or an old snapshot under RepeatableRead or Serializable, and serves them to everyone.
**Accepts:** the check runs when the helper is called, not when its query executes. Generic-path computes aren't checked; "don't compute inside your own open transaction" is documentation there.
**Owner:** `SluiceReadsExtensions.EnsureNoTransaction`, through `ContextTransaction.Of`, which the write side (SLUICE-EF-007) uses too.
**Proof:** `ReadGuardTests.Reads_throw_inside_an_EF_transaction_and_run_again_after_it_commits`, `Reads_throw_inside_an_enlisted_transaction`, `Reads_throw_inside_a_TransactionScope`, `Reads_run_inside_a_TransactionScope_when_the_connection_does_not_enlist`.

## SLUICE-EF-003 EF reads and dependencies refuse types not mapped to a table

**Rule:** the read helpers, `set.Dependency(key)` and `set.AllDependencies()` throw `NotSupportedException` for keyless types and for types mapped with `ToView`, `ToFunction` or `ToSqlQuery`, or to no table.
**Why:** no tracked write ever publishes for those types, so a cached read of one would only ever be evicted by its TTL.
**Accepts:** such data can still be cached through `read.From` with a declared `Source`, invalidated by hand.
**Owner:** `ModelSources.EnsureTable`.
**Proof:** `ReadGuardTests.Reads_and_dependencies_of_types_not_mapped_to_a_table_throw`.

## SLUICE-EF-004 EF keys are typed by the model

**Rule:** a key passed to `read.Entity` or `set.Dependency` must have exactly the CLR type of the single-column primary key, and one passed to `read.Children` the CLR type of the FK property, nullability ignored; otherwise `ArgumentException` names the expected type. Composite primary keys throw `NotSupportedException` for single-row reads and dependencies.
**Why:** a key of another type (`1L` for an `int` key, a raw `int` for a typed id) would format to its own text, so a read and a write of the same row could record different dependencies.
**Owner:** `ModelSources.EnsureKeyType` and `ModelSources.KeyProperty`.
**Proof:** `ReadGuardTests.A_key_of_the_wrong_type_throws_naming_the_expected_type`, `A_nullable_foreign_key_takes_a_key_of_its_underlying_type`, `Reading_or_invalidating_one_row_by_composite_key_throws_but_the_whole_set_works`.

## SLUICE-EF-005 EF key text is the folded column value

**Rule:** a model-derived dependency's key text is the key converted to its provider value through the property's value converter, then folded: `decimal` scale normalised, strings right-trimmed and upper-cased invariantly, `DateTime` (local converted to UTC) and `DateTimeOffset` (UTC) as ticks truncated to whole microseconds, `byte[]` as hex, other `IFormattable` values invariant.
**Why:** values the database treats as equal (`1m` and `1.00m` in `numeric`, `"abc"` and `"ABC"` in `citext`) must give the same dependency, or a write keyed one way misses a reader keyed the other.
**Accepts:** folding can merge values the database tells apart, which only over-invalidates. Accent- and culture-sensitive string collations aren't folded.
**Owner:** `ModelSources.Build` (with `ModelSources.ToProvider`) and `KeyFolding.Fold`.
**Proof:** `FoldedKeyTests.A_numeric_key_read_as_1_is_evicted_by_the_same_value_written_as_1_00`, `A_citext_key_read_in_one_case_is_evicted_by_the_same_key_in_another`, `A_strongly_typed_key_folds_the_value_its_converter_stores`, `KeyFoldingTests.*`.

## SLUICE-EF-006 Model-derived sources are named by EF's root entity type

**Rule:** an entity type's source is named `IEntityType.GetRootType().Name`, and a foreign key's source `<root type name>[<FK property names, comma-joined>]`, built once per model through the core's public `Source<object>(name, format)` constructor and kept as runtime annotations on the model. Writes use the same sources, so a saved row of a derived type invalidates under its root type's name.
**Why:** reads and writes of a derived type (TPH, TPT) must meet on one source, and a `Children` read in any form (FK property, shadow FK, navigation) must meet the writes to that foreign key. The names are wire format: changing them changes every EF tag.
**Owner:** `ModelSources.WrittenEntity` (which `ModelSources.Entity` checks and returns) and `ModelSources.ForeignKey`.
**Proof:** `EntityReadTests.A_derived_type_shares_its_root_type_dependencies`, `ChildrenReadTests.Each_Children_form_depends_on_the_foreign_key_value_it_reads`; for writes, `WriteDerivationTests.Editing_a_derived_row_evicts_readers_of_its_root_type_with_TPH_or_TPT`.

## SLUICE-EF-007 A tracked write is invalidated after it commits, before the call that committed it returns

**Rule:** a tracked save's invalidations are published only once the transaction it ran in has ended, and before the call that ended it returns. Without a transaction that call is `SaveChanges` itself, after its write has run (EF's implicit transaction, if it used one, has committed by then). Inside an EF transaction it's the `Commit` or `Rollback` that ends it, on whichever context makes the call; a transaction disposed without either publishes nothing. Inside an enlisted (`Database.EnlistTransaction`) or ambient (`TransactionScope`) transaction it's whatever completes it, whatever the outcome: `Commit`, `Rollback` or the scope's `Dispose`. The transaction is the one EF's batch executor writes in (the context's current EF transaction, else the enlisted one, else the ambient one), so a connection with `Enlist=false` inside a scope, which EF autocommits, publishes in `SaveChanges`. Every context using one `SluiceCache` gets the same interceptor, so a commit on one context publishes the saves other contexts made in its transaction.
**Why:** publishing before the commit lets a reader cache the old row again between the publish and the commit, and nothing evicts it until its duration ends. Contexts sharing one transaction see different EF transaction ids but the same `DbTransaction`, and only the context that commits sees the commit. An execution strategy that retries a transaction runs each attempt in a new transaction, so only the attempt that commits publishes; a failed attempt's writes are dropped with it, or published if its commit failed (SLUICE-EF-008).
**Accepts:** a promoted or distributed transaction can complete after `TransactionScope.Dispose` returns, and publishes then. A `TransactionScope` without `TransactionScopeAsyncFlowOption.Enabled` doesn't flow across awaits, so after an async save Sluice can see no transaction and publish at once, before the scope completes. Several saves inside one enlisted or ambient transaction publish in several batches, one per save. A `DbTransaction` committed outside EF isn't seen, and neither is a commit on a context without `UseSluice`. A crash between commit and publish loses the invalidation; the entry's duration bounds it.
**Owner:** `SluiceInterceptor`.
**Proof:** `PublishTimingTests.Without_a_transaction_a_save_is_evicted_only_once_its_write_has_run` (a reader that gets in just before the write runs finds nothing published), `Inside_an_EF_transaction_a_save_is_evicted_only_once_the_transaction_commits`, `A_transaction_disposed_without_commit_evicts_nothing_even_when_its_connection_commits_later` (both start the next transaction sync and async), `A_save_on_a_context_sharing_another_context_s_transaction_is_evicted_when_it_commits`, `A_transaction_the_execution_strategy_retries_is_evicted_when_its_retry_commits` (a failed save and a failed commit); `AmbientTransactionTests.Inside_a_TransactionScope_a_save_is_evicted_only_once_the_scope_completes_whatever_its_outcome`, `Inside_an_enlisted_transaction_a_save_is_evicted_only_once_it_completes_whatever_its_outcome`, `Contexts_saving_inside_one_TransactionScope_are_all_evicted_when_it_completes`, `With_Enlist_false_a_save_inside_a_TransactionScope_is_evicted_at_once_and_outlives_the_scope`; `WriteDerivationTests.Saving_an_entity_evicts_readers_of_its_key_and_only_those`.

## SLUICE-EF-008 Every save outcome that might have committed something publishes

**Rule:** a save that fails (`SaveChangesFailed`), is cancelled (`SaveChangesCanceled`) or hits a concurrency conflict (`ThrowingConcurrencyException`, which EF reports instead of `SaveChangesFailed`) publishes what it captured, by the same timing rule as a successful save (SLUICE-EF-007). A save that goes on past a conflict another interceptor suppressed publishes again when it completes. A rolled-back transaction and a failed commit publish what their saves wrote. A transaction failure EF reports and carries on from (a savepoint that failed to release) publishes what the transaction's saves wrote so far, and the transaction's commit or rollback still publishes everything. A save made from inside another save on the same context (an interceptor that saves again from `SavingChanges` or `SavedChanges`) publishes what both wrote.
**Why:** without a transaction, or under `AutoTransactionBehavior.Never`, earlier batches commit before a later one fails, and a failed commit's outcome is unknown. EF reports the end of an inner save before the outer one's, so an outer save forgotten at that point is never published. Over-invalidating is safe; a dropped invalidation leaves stale values until their duration ends.
**Accepts:** a failed save over-invalidates whatever it captured, including rows that never committed. A savepoint failure publishes the transaction's writes before it commits, so they're published twice.
**Owner:** `SluiceInterceptor`.
**Proof:** `PublishTimingTests.A_save_that_fails_after_an_earlier_batch_committed_evicts_that_batch`, `A_concurrency_conflict_after_an_earlier_batch_committed_evicts_that_batch` (both sync and async), `A_save_that_goes_on_past_a_suppressed_concurrency_conflict_evicts_what_it_commits_after_it`, `A_save_cancelled_after_an_earlier_batch_committed_evicts_that_batch` (async only: a sync save takes no token, so the sync cancel handler has no test), `A_rolled_back_transaction_still_evicts_what_its_saves_wrote`, `A_commit_that_fails_still_evicts_what_its_saves_wrote`, `A_savepoint_that_fails_to_release_does_not_stop_the_commit_evicting_every_save_in_the_transaction` (sync and async), `A_save_an_interceptor_makes_from_inside_another_save_evicts_what_both_saves_wrote` (the `SavedChanges` case; the `SavingChanges` case already captures both saves' rows in the inner save).

## SLUICE-EF-009 Publishing after a commit never throws and can't be cancelled

**Rule:** every publish catches any exception and logs it at `Error`, event id 1, through `ILogger<SluiceInterceptor>`, resolved from the services passed to `UseSluice` (which throws if logging isn't registered); the save, commit or transaction completion returns normally. Publishing goes through the core's token-less `InvalidateAsync` and `Invalidate`, and ignores the token EF passes the interceptor.
**Why:** a throw after the commit makes callers retry a write that already committed; one from a transaction's completion event surfaces in whatever completed it, on its thread. A request cancelled just after its commit must still invalidate, or the old value stays cached until its duration ends.
**Accepts:** a failed publish leaves stale values until their duration ends, visible only in the log. A slow distributed cache delays `SaveChanges`; FusionCache's `DistributedCacheHardTimeout` or circuit breaker bounds it. Inside a transaction the dependencies are derived at `SavedChanges`, before the commit, where a failure still throws.
**Owner:** `SluiceInterceptor.Publish` and `SluiceInterceptor.PublishAsync`.
**Proof:** `PublishTimingTests.A_failure_to_invalidate_after_commit_is_logged_and_the_write_returns_normally` (async save, sync save, commit and a completing `TransactionScope`), `Cancelling_the_request_just_after_its_save_committed_still_evicts`, `Cancelling_the_request_just_after_its_transaction_committed_still_evicts`.

## SLUICE-EF-010 Sync EF calls publish synchronously

**Rule:** sync `SaveChanges`, `Commit`, `Rollback` and their failure paths publish through `SluiceCache.Invalidate`, never by blocking on the async path. So does an enlisted or ambient transaction's `TransactionCompleted` event, which is synchronous and can run on another thread.
**Why:** sync-over-async can deadlock under a synchronization context and ties up a thread-pool thread per call.
**Owner:** `SluiceInterceptor.Publish`.
**Proof:** `PublishTimingTests.Sync_SaveChanges_Commit_and_Rollback_evict_without_going_through_async_code`, `AmbientTransactionTests.A_completing_TransactionScope_evicts_without_going_through_async_code` (their distributed cache throws on any async call, which FusionCache logs as a warning; the first test shows an async call does log one).

## SLUICE-EF-011 Capturing a save doesn't change what EF saves

**Rule:** a save on a context with `UseSluice` writes exactly what it would write without it. An edit EF wouldn't detect (with `AutoDetectChangesEnabled` off) stays unsaved and isn't invalidated, and a cascade the app's timings leave to the database or forbid (a timing of `Never`) isn't applied.
**Why:** an unconditional `DetectChanges()` makes EF save edits the app chose not to detect. EF's public `CascadeChanges()` forces every cascade, including orphan deletes the app set to `Never`.
**Accepts:** with one timing `OnSaveChanges` and the other `Never`, the cascades EF applies during the save aren't captured, so point readers of the rows it cascades aren't evicted; `Children` readers still are (SLUICE-EF-012). `CascadeChanges()` (when it runs) and `Entries()` each scan the tracker before EF scans it to save, so a save may scan it up to three times.
**Owner:** `CapturedSave.Capture`.
**Proof:** `WriteDerivationTests.With_change_detection_off_an_undetected_edit_is_neither_saved_nor_invalidated`, `With_orphan_deletion_off_Sluice_does_not_make_EF_delete_an_orphan`, `With_cascades_on_save_deleting_a_principal_evicts_readers_of_its_tracked_dependents`.

## SLUICE-EF-012 A save invalidates real keys and its foreign keys before and after

**Rule:** each changed row invalidates its key and, for each foreign key, its value before and after the save, skipping nulls, so a foreign key set to or from null invalidates the one principal it had. A many-to-many join row and a composite-key row are changed rows like any other. Values before the save are read in `SavingChanges`; keys and values after it are read once the save has run. When the tracker can't vouch for a foreign key's value before the save, the save invalidates every `Children` reader of that foreign key instead: when the key is marked modified with its original value equal to its current one (what `Update()` on an object the context didn't load produces), and when its original value is one the database can't hold (null in a required foreign key, or the default of a non-nullable type, such as 0, on an attached or removed stub). A primary key that's temporary again after a failed save invalidates its whole entity type, and a foreign key whose current value is still temporary invalidates every `Children` reader of that foreign key. A deleted row also invalidates every foreign key that references it, keyed by that foreign key's principal key (an alternate key included). A write to a type the read helpers refuse still publishes and doesn't fail the rest of the save.
**Why:** EF overwrites original values when it accepts the save, so a re-parented row's old parent would be missed; an object the context never loaded has no original values at all. Generated keys are temporary until the save runs, and a failed save makes them temporary again, though an earlier batch may have committed the rows under them. The database's `CASCADE` and `SET NULL` change rows the tracker never loaded.
**Accepts:** `Update()` and stubs evict every `Children` reader of the foreign keys they can't vouch for, and a failed insert every reader of its type. A stub whose nullable foreign key was left null looks like a row with no parent, so its old parent's `Children` readers aren't evicted: load the row first, or invalidate `set.AllDependencies()`. Point readers of rows the database cascades without the tracker loading them aren't evicted; after such a delete, invalidate `set.AllDependencies()`. Entities an interceptor changes in a `SavingChanges` that runs after Sluice's (one added after `UseSluice`) aren't captured; `UseSluice` says to add Sluice last.
**Owner:** `CapturedSave`.
**Proof:** `WriteDerivationTests.Re_parenting_a_child_evicts_Children_readers_of_both_parents`, `Re_parenting_through_a_navigation_or_a_shadow_foreign_key_evicts_every_Children_form_of_both_parents`, `Removing_a_row_that_was_never_loaded_evicts_Children_readers_of_its_parent`, `Updating_a_row_from_an_object_that_was_never_loaded_evicts_Children_readers_of_its_old_parent`, `A_partial_update_of_a_row_that_was_never_loaded_evicts_Children_readers_of_its_parent`, `Inserting_rows_evicts_earlier_not_found_reads_of_their_generated_keys` (and not a read of another customer), `Deleting_a_principal_evicts_Children_readers_of_rows_the_database_deletes_or_sets_to_null`, `Adding_editing_or_deleting_a_child_evicts_Children_readers_of_its_parent_only`, `Setting_a_nullable_foreign_key_to_or_from_null_evicts_Children_readers_of_its_principal`, `A_foreign_key_to_an_alternate_key_evicts_Children_readers_of_that_key`, `Linking_or_unlinking_many_to_many_rows_evicts_Children_readers_of_the_join_rows_on_both_sides`, `Writing_a_composite_key_row_evicts_its_Children_and_All_readers`, `A_save_that_also_writes_a_type_reads_refuse_still_evicts_the_rest`; `PublishTimingTests.A_row_inserted_by_a_committed_batch_of_a_failed_save_evicts_readers_of_its_type`, `A_row_inserted_by_a_committed_batch_of_a_failed_save_evicts_Children_readers_of_its_new_parent`.

## SLUICE-EF-013 A changed owned row invalidates its owner as a modified row

**Rule:** an owned entry that is Added, Modified or Deleted is captured as its owner, found among the tracked entries by the ownership's key and walked up through owned owners to the first non-owned one, in state Modified: the owner's key and its foreign keys before and after. An owner that changed itself is captured in its own right. Nothing is derived from the owned entry's own type, which no read helper can read.
**Why:** EF tracks an owned value as its own entry and leaves the owner Unchanged, but readers only see owned values through the owner: its `Entity` and `All` readers, and `Children` readers of the owner's foreign keys.
**Accepts:** each changed owned row scans the tracked entries for its owner, so a save that changes many owned rows on a large tracker pays for both.
**Owner:** `CapturedSave.Capture` (with `CapturedSave.OwnerOf`).
**Proof:** `WriteDerivationTests.Changing_an_owned_value_evicts_readers_of_its_owner_though_the_tracker_leaves_the_owner_unchanged` (a value owned by an owned value, and owned collection rows added and removed; it asserts the owner stays Unchanged).

## SLUICE-EF-014 Pooled contexts and context factories publish every lease's writes, and only those

**Rule:** contexts from `AddDbContextPool((sp, o) => …)`, `AddPooledDbContextFactory` and `AddDbContextFactory`, whose options and interceptors are built once and kept for the container's lifetime, invalidate what each lease commits. A pooled instance never publishes what an earlier lease on it left uncommitted.
**Why:** a pooled instance serves one lease after another with the options and interceptors fixed when the pool was built; anything Sluice kept per instance would leak one lease's writes into another's publish, or lose them.
**Accepts:** a save whose concurrency conflict nobody suppressed leaves its capture on the context, and the context's next save, on the same lease or a later one, publishes it again with its own (SLUICE-EF-008's nested-save rule carries it), which only over-invalidates.
**Owner:** `SluiceInterceptor`.
**Proof:** `PoolingTests.Each_lease_evicts_what_it_commits_and_nothing_an_earlier_lease_left_uncommitted` (all three registrations; it asserts the pools hand the second lease the same instance).
