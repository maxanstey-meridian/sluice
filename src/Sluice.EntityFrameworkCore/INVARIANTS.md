# Invariants

Rules a reasonable-looking change could break. Each names the one place that enforces it and the tests that fail if it stops. Test names are in `tests/Sluice.EntityFrameworkCore.Tests`. The core's rules (`SLUICE-CORE-`) are in `src/Sluice/INVARIANTS.md`. User-facing docs: `docs/guides/ef-core.md`, `strict-reads.md` and `transactions.md`.

## SLUICE-EF-001 EF reads never track, and load no auto-included navigation

**Rule:** `read.Entity`, `read.Children` and `read.All` return `AsNoTracking()` queries that call `IgnoreAutoIncludes()`, and `AsTracking()` on one throws (SLUICE-EF-016). Owned navigations still load: EF ignores `IgnoreAutoIncludes()` for ownership.
**Why:** a tracked query hands back the context's own instance, so the cache would store the caller's unsaved edits for everyone; an auto-included navigation reads rows the helper's dependency doesn't cover, with no `Include` for the shape check to see.
**Accepts:** cached entities are detached; to edit one, load it again.
**Owner:** `StrictReads.Query`, which every helper's query starts from.
**Proof:** `EntityReadTests.Reads_do_not_track_so_an_unsaved_edit_on_the_callers_context_is_not_cached`, `StrictReadTests.A_read_helper_does_not_load_auto_included_navigations`.

## SLUICE-EF-002 EF reads refuse to run inside a transaction

**Rule:** while a compute runs, a command carrying the helpers' marker, or a read in a fused fetch (SLUICE-CORE-013), throws `InvalidOperationException` when it executes inside the context's EF transaction, an enlisted one or an ambient one, read from the context when the command runs, as EF's batch executor reads it (so `Enlist=false` doesn't count).
**Why:** a compute inside a transaction caches rows others can't see yet, which may roll back, or an old snapshot, and serves them to everyone. A query built outside a transaction can run inside one, so the check is at execution.
**Accepts:** generic-path computes aren't checked. A raw `DbTransaction` on the context's connection isn't seen. Outside a compute the helpers run inside transactions, since nothing is cached.
**Owner:** `StrictReads.Check`, through `ContextTransaction.Of`, which the write side (SLUICE-EF-007) uses too.
**Proof:** `ReadGuardTests.Reads_throw_inside_an_EF_transaction_and_run_again_after_it_commits`, `Reads_throw_inside_an_enlisted_transaction`, `Reads_throw_inside_a_TransactionScope`, `Reads_run_inside_a_TransactionScope_when_the_connection_does_not_enlist`; `StrictReadTests.A_read_helper_query_built_outside_a_transaction_throws_when_it_runs_inside_one`, `A_compute_that_catches_a_strict_read_violation_caches_nothing_and_its_caller_still_gets_it` (the fused-fetch cases).

## SLUICE-EF-003 EF reads and dependencies refuse types not mapped to a table

**Rule:** the read helpers, `set.Dependency(key)` and `set.AllDependencies()` throw `NotSupportedException` for keyless types and types mapped with `ToView`, `ToFunction`, `ToSqlQuery` or to no table. An abstract TPC root is mapped when every concrete type under it is.
**Why:** no tracked write publishes for those types, so a cached read of one would only expire.
**Accepts:** such data is cached through a fused fetch with a declared `Source`, invalidated by hand.
**Owner:** `ModelSources.EnsureTable`.
**Proof:** `ReadGuardTests.Reads_and_dependencies_of_types_not_mapped_to_a_table_throw`; for the TPC root, `EntityReadTests.A_derived_type_shares_its_root_type_dependencies` and `WriteDerivationTests.Editing_a_derived_row_evicts_readers_of_its_root_type_with_TPH_TPT_or_TPC`.

## SLUICE-EF-004 EF keys are typed by the model

**Rule:** a key passed to `read.Entity` or `set.Dependency` has exactly the CLR type of the single-column primary key, and one passed to `read.Children` that of the FK property, nullability ignored; otherwise `ArgumentException` names the expected type. Composite primary keys throw `NotSupportedException` for single-row reads and dependencies.
**Why:** a key of another type (`1L` for an `int` key, a raw value for a typed id) formats to its own text, so a read and a write of one row could record different dependencies.
**Owner:** `ModelSources.EnsureKeyType` and `ModelSources.KeyProperty`.
**Proof:** `ReadGuardTests.A_key_of_the_wrong_type_throws_naming_the_expected_type`, `A_nullable_foreign_key_takes_a_key_of_its_underlying_type`, `Reading_or_invalidating_one_row_by_composite_key_throws_but_the_whole_set_works`.

## SLUICE-EF-005 EF key text is the folded column value

**Rule:** a model-derived key's text is its provider value, through the property's value converter, then folded: `decimal` scale normalised, strings right-trimmed and upper-cased invariantly, `DateTime` (local to UTC) and `DateTimeOffset` as ticks truncated to microseconds, `byte[]` as hex, other `IFormattable` values invariant.
**Why:** values the database treats as equal (`1m` and `1.00m` in `numeric`, `abc` and `ABC` in `citext`) must give one dependency, or a write keyed one way misses a reader keyed the other.
**Accepts:** folding can merge values the database tells apart, which only over-invalidates. Accent- and culture-sensitive collations aren't folded.
**Owner:** `ModelSources.Build` (with `ModelSources.ToProvider`) and `KeyFolding.Fold`.
**Proof:** `FoldedKeyTests.A_numeric_key_read_as_1_is_evicted_by_the_same_value_written_as_1_00`, `A_citext_key_read_in_one_case_is_evicted_by_the_same_key_in_another`, `A_strongly_typed_key_folds_the_value_its_converter_stores`, `KeyFoldingTests.*`.

## SLUICE-EF-006 Model-derived sources are named by EF's root entity type

**Rule:** an entity type's source is named `IEntityType.GetRootType().Name`, and a foreign key's `<root type name>[<FK property names, comma-joined>]`, built once per model through the core's public `Source<object>(name, format)` and kept as model annotations. Writes use the same sources.
**Why:** reads and writes of derived types (TPH, TPT, TPC), and every `Children` form, must meet on one source. The names are wire format.
**Owner:** `ModelSources.WrittenEntity` (which `ModelSources.Entity` checks and returns) and `ModelSources.ForeignKey`.
**Proof:** `EntityReadTests.A_derived_type_shares_its_root_type_dependencies`, `ChildrenReadTests.Each_Children_form_depends_on_the_foreign_key_value_it_reads`, `WriteDerivationTests.Editing_a_derived_row_evicts_readers_of_its_root_type_with_TPH_TPT_or_TPC`.

## SLUICE-EF-007 A tracked write is invalidated after it commits, before the call that committed it returns

**Rule:** a save's invalidations publish once the transaction it ran in has ended, before the call that ended it returns: `SaveChanges` without a transaction; `Commit` or `Rollback` of an EF transaction, on whichever context calls it (disposed without either, nothing); completion of an enlisted or ambient transaction, whatever the outcome. The transaction is read when the save completes, as EF's batch executor reads it. Every context on one `SluiceCache` shares the interceptor, so a commit publishes other contexts' saves in its transaction.
**Why:** publishing before the commit lets a reader re-cache the old row in between, and nothing evicts it until it expires.
**Accepts:** a promoted or distributed transaction can complete after `TransactionScope.Dispose` returns. A scope without `TransactionScopeAsyncFlowOption.Enabled` can leave an async save seeing no transaction, so it publishes at once. Ambient saves publish one batch each, on the completing thread; an EF transaction publishes one de-duplicated batch. A leftover save (SLUICE-EF-008) publishes early, then again from its live transaction. A `DbTransaction` committed outside EF, a commit on a context without `UseSluice`, and a crash between commit and publish aren't covered.
**Owner:** `SluiceInterceptor`.
**Proof:** `PublishTimingTests.Without_a_transaction_a_save_is_evicted_only_once_its_write_has_run`, `Inside_an_EF_transaction_a_save_is_evicted_only_once_the_transaction_commits`, `A_transaction_disposed_without_commit_evicts_nothing_even_when_its_connection_commits_later`, `A_save_on_a_context_sharing_another_context_s_transaction_is_evicted_when_it_commits`, `A_transaction_the_execution_strategy_retries_is_evicted_when_its_retry_commits`, `A_save_whose_transaction_an_interceptor_commits_before_Sluice_sees_the_save_complete_is_evicted`; `AmbientTransactionTests.Inside_a_TransactionScope_a_save_is_evicted_only_once_the_scope_completes_whatever_its_outcome`, `Inside_an_enlisted_transaction_a_save_is_evicted_only_once_it_completes_whatever_its_outcome`, `Contexts_saving_inside_one_TransactionScope_are_all_evicted_when_it_completes`, `With_Enlist_false_a_save_inside_a_TransactionScope_is_evicted_at_once_and_outlives_the_scope`; `RandomWorkloadTests.Once_concurrent_random_reads_and_writes_settle_every_cached_read_matches_a_fresh_one`.

## SLUICE-EF-008 Every save outcome that might have committed something publishes

**Rule:** a failed, cancelled or conflicting save (`SaveChangesFailed`, `SaveChangesCanceled`, `ThrowingConcurrencyException`) publishes what it captured by SLUICE-EF-007's timing, and a save that goes on past a suppressed conflict publishes again. A rollback, a failed commit and a failed savepoint release publish what the transaction's saves wrote; the later commit or rollback still publishes everything. A capture still held when the context saves again (a leftover: an outer save of a nested save, or one ended without an event) publishes at once, and again from the transaction it began in if that's still live, never from the next save's transaction.
**Why:** earlier batches can commit before a later one fails, and a failed commit's outcome is unknown. EF ends an inner save before its outer one, so an outer save not published by then never would be. Over-invalidating is safe; a dropped invalidation isn't.
**Accepts:** failed saves over-invalidate rows that never committed; a savepoint failure and a leftover publish more than once.
**Owner:** `SluiceInterceptor`.
**Proof:** `PublishTimingTests.A_save_that_fails_after_an_earlier_batch_committed_evicts_that_batch`, `A_concurrency_conflict_after_an_earlier_batch_committed_evicts_that_batch`, `A_save_that_goes_on_past_a_suppressed_concurrency_conflict_evicts_what_it_commits_after_it`, `A_save_cancelled_after_an_earlier_batch_committed_evicts_that_batch` (async only: a sync save takes no token), `A_rolled_back_transaction_still_evicts_what_its_saves_wrote`, `A_commit_that_fails_still_evicts_what_its_saves_wrote`, `A_savepoint_that_fails_to_release_does_not_stop_the_commit_evicting_every_save_in_the_transaction`, `A_save_an_interceptor_makes_from_inside_another_save_evicts_what_both_saves_wrote`, `A_committed_save_is_evicted_though_an_interceptor_then_saves_again_in_a_transaction_it_never_commits`, `Inside_an_EF_transaction_a_save_an_interceptor_saves_again_after_is_evicted_once_more_when_the_transaction_commits`; `AmbientTransactionTests.A_save_left_behind_by_a_conflict_in_a_disposed_TransactionScope_is_evicted_by_the_next_save_without_failing_it`.

## SLUICE-EF-009 Publishing after a commit never throws and can't be cancelled

**Rule:** every publish catches any exception and logs it at `Error`, event id 1, through `ILogger<SluiceInterceptor>` from the services passed to `UseSluice` (which throws without logging); the save, commit or completion returns normally. Publishing uses the core's token-less `InvalidateAsync` and `Invalidate`, ignoring EF's token.
**Why:** a throw after the commit makes callers retry a committed write; a request cancelled just after its commit must still invalidate.
**Accepts:** a failed publish leaves stale values until they expire, visible only in the log. An L2 that fails a tag write doesn't fail the publish: FusionCache writes the tag again once the L2 is back (auto-recovery). A slow L2 delays `SaveChanges` until the Redis client times out, since FusionCache puts no timeout on writes; the client's timeout and FusionCache's circuit breakers bound it. Inside a transaction, deriving the dependencies happens at `SavedChanges`, before the commit, where a failure still throws. A leftover's are derived inside its publish, so a failure to derive them is logged, not thrown into the unrelated save that found it.
**Owner:** `SluiceInterceptor.Publish` and `SluiceInterceptor.PublishAsync`.
**Proof:** `PublishTimingTests.A_failure_to_invalidate_after_commit_is_logged_and_the_write_returns_normally`, `Cancelling_the_request_just_after_its_save_committed_still_evicts`, `Cancelling_the_request_just_after_its_transaction_committed_still_evicts`; for an L2 that fails writes, the core tests' `MultiNodeTests.A_distributed_cache_that_fails_writes_does_not_make_InvalidateAsync_throw_and_the_tag_is_written_once_it_recovers`.

## SLUICE-EF-010 Sync EF calls publish synchronously

**Rule:** sync `SaveChanges`, `Commit`, `Rollback`, their failure paths, and an enlisted or ambient transaction's `TransactionCompleted` event publish through `SluiceCache.Invalidate`, never by blocking on the async path.
**Why:** sync-over-async can deadlock under a synchronization context and ties up a thread-pool thread per call.
**Owner:** `SluiceInterceptor.Publish`.
**Proof:** `PublishTimingTests.Sync_SaveChanges_Commit_Rollback_and_their_failures_evict_without_going_through_async_code`, `AmbientTransactionTests.A_completing_TransactionScope_evicts_without_going_through_async_code`. Their L2 throws on any async call, which FusionCache logs as a warning; the first test shows an async call does log one.

## SLUICE-EF-011 Capturing a save doesn't change what EF saves

**Rule:** a save on a context with `UseSluice` writes exactly what it would without it: an edit EF wouldn't detect (with `AutoDetectChangesEnabled` off) stays unsaved and uninvalidated, and a cascade the app's timings leave to the database or set to `Never` isn't applied.
**Why:** an unconditional `DetectChanges()` saves edits the app chose not to detect, and EF's public `CascadeChanges()` forces every cascade.
**Accepts:** with one timing `OnSaveChanges` and the other `Never`, point readers of rows EF cascades during the save through `ClientCascade` or `ClientSetNull` aren't evicted (`Children` readers are; `Cascade` and `SetNull` are covered by SLUICE-EF-012). The tracker may be scanned up to three times per save.
**Owner:** `CapturedSave.Capture`.
**Proof:** `WriteDerivationTests.With_change_detection_off_an_undetected_edit_is_neither_saved_nor_invalidated`, `With_orphan_deletion_off_Sluice_does_not_make_EF_delete_an_orphan`, `With_cascades_on_save_deleting_a_principal_evicts_readers_of_its_tracked_dependents`.

## SLUICE-EF-012 A save invalidates real keys and its foreign keys before and after

**Rule:** each changed row (join and composite-key rows included) invalidates its key and each foreign key's value before and after the save, skipping nulls; originals are read in `SavingChanges`, keys and current values after the save. A foreign key the tracker can't vouch for invalidates every `Children` reader of it: marked modified with original equal to current (`Update()` of an unloaded object), or an original that's null in a required key or equal to EF's sentinel for the property (the CLR default, such as 0, unless `HasSentinel` says otherwise). A primary key temporary again after a failed save invalidates its type's `All`, and a foreign key still temporary its source's `All`. A deleted row also invalidates every foreign key that references it, by that key's principal value, and whatever the model lets the database do to rows of other types: for each non-ownership foreign key to the row's type (or a base or derived type) whose `DeleteBehavior` is `Cascade` or `SetNull`, the dependent type's `All` (an owned dependent counts as its owner) and every other foreign key of the dependent, whole; for `Cascade`, then the same for the dependent's own cascading foreign keys, each whole, transitively, visiting each type once. Table-per-type links between a derived type's table and its base's are the same row, not a cascade. A row deleted through a base type also invalidates, whole, every foreign key that references only a derived type, since it may be one. `Restrict`, `NoAction`, `ClientCascade` and `ClientSetNull` add nothing. The closure is built once per type. A write to a type the helpers refuse still publishes without failing the save.
**Why:** EF overwrites originals when it accepts the save, and an unloaded object has none; generated keys are temporary until the save runs, and again after a failure; database `CASCADE` and `SET NULL` delete or change rows the tracker never loaded, whose keys only a query before the delete could tell (rejected: an extra round trip per delete, and it races other writers).
**Accepts:** `Update()`, stubs and a real default-valued foreign key (a seeded 0) over-invalidate `Children` readers. A stub whose key holds any other value looks loaded and invalidates the parent it names, so when that isn't the stored one (a nullable key left null, a value from a constructor), the real old parent is missed. A delete over-invalidates by whole type: deleting one customer evicts every cached read of orders (and order lines, and every other type it cascades into). Cascades the model doesn't know about (triggers, a database-first schema that drifted from the model) aren't covered. Changes from an interceptor that runs after Sluice's `SavingChanges` aren't captured. Two entity types splitting one table and sharing a non-key column: saving one changes the column, but only its own root's source is invalidated, so the other's `Entity` and `All` readers keep the old value.
**Owner:** `CapturedSave`, with the cascade closure in `ModelSources.Cascades`.
**Proof:** `WriteDerivationTests.Re_parenting_a_child_evicts_Children_readers_of_both_parents`, `Re_parenting_through_a_navigation_or_a_shadow_foreign_key_evicts_every_Children_form_of_both_parents`, `Removing_a_row_that_was_never_loaded_evicts_Children_readers_of_its_parent`, `Removing_a_row_that_was_never_loaded_with_its_required_foreign_key_set_to_null_evicts_Children_readers_of_its_parent`, `Updating_a_row_from_an_object_that_was_never_loaded_evicts_Children_readers_of_its_old_parent`, `A_partial_update_of_a_row_that_was_never_loaded_evicts_Children_readers_of_its_parent`, `Inserting_rows_evicts_earlier_not_found_reads_of_their_generated_keys`, `Deleting_a_principal_evicts_Children_readers_of_rows_the_database_deletes_or_sets_to_null`, `Adding_editing_or_deleting_a_child_evicts_Children_readers_of_its_parent_only`, `Setting_a_nullable_foreign_key_to_or_from_null_evicts_Children_readers_of_its_principal`, `A_foreign_key_to_an_alternate_key_evicts_Children_readers_of_that_key`, `Linking_or_unlinking_many_to_many_rows_evicts_Children_readers_of_the_join_rows_on_both_sides`, `Writing_a_composite_key_row_evicts_its_Children_and_All_readers`, `A_save_that_also_writes_a_type_reads_refuse_still_evicts_the_rest`, `Deleting_a_principal_evicts_every_reader_of_the_rows_the_database_cascades_into_at_any_depth`, `Deleting_a_principal_evicts_every_reader_of_the_rows_the_database_sets_to_null`, `Deleting_a_principal_evicts_nothing_more_for_dependents_the_database_leaves_alone`, `A_cascade_from_a_type_to_itself_evicts_readers_at_every_depth_and_ends`, `Deleting_a_row_evicts_no_reader_of_another_row_of_its_type`, `Deleting_a_derived_row_through_a_base_type_stub_evicts_lists_through_foreign_keys_into_the_derived_type`; `PublishTimingTests.A_row_inserted_by_a_committed_batch_of_a_failed_save_evicts_readers_of_its_type`, `A_row_inserted_by_a_committed_batch_of_a_failed_save_evicts_Children_readers_of_its_new_parent`; `RandomWorkloadTests.After_each_random_write_every_cached_read_matches_a_fresh_one`.

## SLUICE-EF-013 A changed owned row invalidates its owner as a modified row

**Rule:** an owned entry that is Added, Modified or Deleted is captured as its first non-owned owner, found among the tracked entries by the ownership key, in state Modified. Nothing is derived from the owned type itself.
**Why:** EF tracks an owned value as its own entry and leaves the owner Unchanged, but readers only see owned values through the owner.
**Accepts:** each changed owned row scans the tracked entries for its owner.
**Owner:** `CapturedSave.Capture` (with `CapturedSave.OwnerOf`).
**Proof:** `WriteDerivationTests.Changing_an_owned_value_evicts_readers_of_its_owner_though_the_tracker_leaves_the_owner_unchanged`.

## SLUICE-EF-014 Pooled contexts and context factories publish every lease's writes, and only those

**Rule:** contexts from `AddDbContextPool((sp, o) => …)`, `AddPooledDbContextFactory` and `AddDbContextFactory` invalidate what each lease commits, and nothing an earlier lease on the same instance left uncommitted, beyond leftovers that only over-invalidate.
**Why:** a pooled instance keeps its options and interceptors across leases, so per-instance state would leak one lease's writes into another's publish, or lose them.
**Accepts:** a save that ends without an event (an unsuppressed conflict, or an interceptor after Sluice throwing in `SavingChanges`; EF raises `SavingChanges` outside its try, so no failure event fires) stays on the context until its next save, which publishes it (SLUICE-EF-008).
**Owner:** `SluiceInterceptor`.
**Proof:** `PoolingTests.Each_lease_evicts_what_it_commits_and_nothing_an_earlier_lease_left_uncommitted`; for a leftover, `AmbientTransactionTests.A_save_left_behind_by_a_conflict_in_a_disposed_TransactionScope_is_evicted_by_the_next_save_without_failing_it`.

## SLUICE-EF-015 Inside a compute, every EF command comes from a read helper

**Rule:** the helpers tag their queries with `StrictReads.Marker`. While a compute runs, every write a `UseSluice` context executes (`SaveChanges`, `ExecuteSql`, `ExecuteUpdate`, `ExecuteDelete`), told apart by the command's source, throws `InvalidOperationException`, a fused fetch included; and every other command whose SQL lacks the marker throws unless it's a read in a fused fetch (SLUICE-CORE-013). The message names the command by its first line. The violation is recorded on the compute (SLUICE-CORE-007). Outside a compute nothing is checked.
**Why:** a read not made through a helper records no dependency, so the value built from it goes stale silently; a write inside a compute publishes mid-compute.
**Accepts:** `FromSql`, `SqlQuery`, compiled queries, navigations and lazy loading work inside a compute only in a fused fetch. Reads that run no SQL (`Find` of a tracked entity, `Local`, loaded navigations), raw ADO.NET on the context's connection, a context without `UseSluice`, a copied marker and work under `ExecutionContext.SuppressFlow` aren't checked. A helper query built in one compute and run in another records on the first. The message can show constants inlined in SQL.
**Owner:** `StrictReads.Check`, called from `StrictReads.CommandInitialized`.
**Proof:** `StrictReadTests.A_command_not_built_by_a_read_helper_throws_inside_a_compute_and_runs_outside_one`, `A_bulk_write_on_a_read_helper_query_throws_inside_a_compute_and_runs_outside_one`, `Lazy_and_explicit_loading_throw_inside_a_compute_and_run_outside_one`, `A_compute_that_catches_a_strict_read_violation_caches_nothing_and_its_caller_still_gets_it`, `A_plain_query_after_a_read_From_that_only_records_still_throws`; for the fused fetch, SLUICE-CORE-013's proofs.

## SLUICE-EF-016 A read helper's query reads only its own rows, doesn't track, and returns no entity that holds a context or loads lazily

**Rule:** a marked query reads only its root type's own rows (owned types included), doesn't track, and returns no entity that holds a context or loads lazily (or owns a value that does); the root type's query filters obey the same rule and may not read the context's state, unless the query ignores them. A breach throws `InvalidOperationException` when EF compiles the query, inside a compute or not, and is recorded on a running compute.
**Why:** a row the dependency doesn't cover is cached with no dependency on it, a context-reading filter gives each context different rows behind one cached value, and a lazy or context-holding entity reads later through its context. EF runs the interceptor only on compile, so a check limited to computes would let a tree first compiled outside one through. The check is one process-wide singleton interceptor, since EF builds an internal service provider per distinct set of them and throws after twenty.
**Accepts:** a helper's query used for a bulk write outside a compute runs. `UseInternalServiceProvider` contexts can't use Sluice. A query expression interceptor added after `UseSluice` or in `OnConfiguring` rewrites after the check. Functions mapped with `IsBuiltIn = true` are trusted. A lazy entity inside an app type isn't seen, and an `object`-typed value in such a result is refused. A member name that's a navigation on any assignable entity type is refused, and so is `o.Customer!.Id`. A filter reading non-context state is frozen into the SQL when the query first compiles; that's wrong with or without Sluice.
**Owner:** `StrictReads.QueryCompilationStarting` and its `ShapeCheck`.
**Proof:** `StrictReadTests.A_read_helper_query_that_reads_beyond_its_own_rows_tracks_or_returns_lazy_entities_throws_inside_a_compute`, `A_compute_that_catches_a_strict_read_violation_caches_nothing_and_its_caller_still_gets_it`, `A_read_helper_query_may_read_owned_values_and_shape_its_own_rows`, `A_read_helper_query_may_keep_query_filters_on_its_own_columns_or_ignore_them`, `A_read_helper_query_that_reads_beyond_its_own_rows_throws_outside_a_compute_too_and_every_time_it_runs`.

## SLUICE-EF-018 The read helpers refuse a context the checks don't run on

**Rule:** `read.Entity`, `read.Children` and `read.All` throw `InvalidOperationException` unless the context's options carry the strict-read interceptor `UseSluice` adds.
**Why:** the checks of SLUICE-EF-002, 015 and 016 live in that interceptor, so without it a helper would read with them silently off.
**Accepts:** the helpers also throw `NotSupportedException` for a non-relational provider; that's untested, since the suite's only provider is PostgreSQL.
**Owner:** `StrictReads.Query`.
**Proof:** `ReadGuardTests.Reads_on_a_context_without_UseSluice_throw`.

## SLUICE-EF-019 `set.AllDependencies()` covers the type and every row a delete of it cascades into

**Rule:** `set.AllDependencies()` is the type's `All`, every foreign key the type or a derived type declares, whole, every foreign key pointing at it that cascades or sets to null, whole, and the same cascade closure a tracked delete publishes (SLUICE-EF-012). `set.Dependency(key)` stays the one row's dependency, with no cascades.
**Why:** it's what an app invalidates after a bypass such as `ExecuteDelete`, and the database applies the same `ON DELETE` rules to the rows a bypass deletes as to those a save deletes.
**Accepts:** after an `ExecuteUpdate` that deletes nothing, the dependent types are evicted for nothing. The over-invalidation and the model-unknown cascades of SLUICE-EF-012.
**Owner:** `DbSetDependencyExtensions.AllDependencies`, with `ModelSources.CascadingForeignKeys` and `ModelSources.Cascades`.
**Proof:** `WriteDerivationTests.AllDependencies_of_a_principal_set_evicts_readers_of_the_rows_an_ExecuteDelete_cascades_into`, `EntityReadTests.An_entity_read_returns_its_row_and_is_evicted_by_its_key_and_by_AllDependencies_only`, `An_All_read_is_evicted_by_any_key_of_its_set_and_by_AllDependencies_but_not_by_another_set`, `ChildrenReadTests.A_Children_read_is_evicted_by_AllDependencies_of_its_set_but_not_by_its_principal`.
