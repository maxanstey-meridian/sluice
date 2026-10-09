# Invariants

Rules a reasonable-looking change could break. Each names the one place that enforces it and the test that fails if it stops. `SLUICE-CORE-` test names are in `tests/Sluice.Tests`, `SLUICE-EF-` ones in `tests/Sluice.EntityFrameworkCore.Tests`.

## SLUICE-CORE-001 The core references no EF or data-access library

**Rule:** `Sluice` and everything it transitively references contain no `Microsoft.EntityFrameworkCore*`, `Microsoft.Data*`, `System.Data*`, `Npgsql*` or `Dapper` assembly or package. EF support lives only in `Sluice.EntityFrameworkCore`.
**Why:** the core is source-agnostic: Dapper, HTTP and file users must not pull in EF.
**Owner:** `src/Sluice/Sluice.csproj` (its package references).
**Proof:** `CoreReferenceTests.The_core_assembly_references_no_EF_or_data_access_library`, `The_core_package_graph_contains_no_EF_or_data_access_package` (the compiler drops unused references from assembly metadata, so only the package graph sees a referenced but unused package).

## SLUICE-CORE-002 Invalidation removes entries; it never leaves them as fail-safe fallbacks

**Rule:** the Sluice FusionCache runs with `DisableTagging = false` and `RemoveByTagBehavior.Remove`, both applied after the user's configuration, and `ClearAsync` calls `ClearAsync(allowFailSafe: false)`.
**Why:** under FusionCache's default `Expire`, fail-safe and soft timeouts serve a value that was just invalidated whenever the compute fails or is slow. With tagging disabled, FusionCache throws on every invalidation.
**Accepts:** an invalidated entry is gone even when the data source is down, so the caller gets the error instead of stale data.
**Owner:** `SluiceServiceCollectionExtensions.AddSluice` (behaviour) and `SluiceCache.ClearAsync`.
**Proof:** `FusionCacheConfigurationTests.With_fail_safe_on_a_failing_compute_after_invalidation_throws_instead_of_serving_the_invalidated_value` (its configuration sets `RemoveByTagBehavior.Expire`, so Sluice's override must win), `With_fail_safe_on_a_failing_compute_after_ClearAsync_throws_instead_of_serving_the_cleared_value`, `A_user_who_disables_tagging_still_gets_invalidation`.

## SLUICE-CORE-003 A compute finishes before its caller resumes, and its entry is stamped when it started

**Rule:** every `GetOrSetAsync` call passes entry options with `FactorySoftTimeout` and `FactoryHardTimeout` infinite, `AllowTimedOutFactoryBackgroundCompletion = false` and `EagerRefreshThreshold = null`, whatever the user configured. It passes FusionCache no cancellation token: the caller's token goes to the compute, and fused reads hand it to their fetches. A token that is already cancelled throws before the cache is read, so that caller gets neither a hit nor a compute.
**Why:** a timed-out factory keeps using the caller's scoped resources (a `DbContext`, a connection) after the caller moved on, and background completion and eager refresh stamp the entry when the compute *finishes*, which lets an invalidation that landed mid-compute be ignored. On a cancelled token FusionCache throws without awaiting its factory, so a token passed to it would let a cancelled caller resume while its compute still runs.
**Accepts:** a slow data source makes the caller wait; FusionCache timeouts don't apply to Sluice computes. Cancelling doesn't interrupt a wait on another caller's compute (FusionCache's memory lock) or a slow L2 read; `LockTimeout` and the L2 timeouts bound those.
**Owner:** the `SluiceCache` constructor (`_entryOptions`) and `SluiceCache.GetOrSetAsync` (the token).
**Proof:** `FusionCacheConfigurationTests.A_compute_outliving_the_factory_hard_timeout_completes_before_the_caller_resumes`, `A_compute_outliving_the_factory_soft_timeout_completes_before_the_caller_resumes`, `A_cancelled_caller_resumes_only_after_its_compute_ends`, `An_already_cancelled_caller_gets_neither_a_hit_nor_a_compute`; end to end through EF, the EF tests' `EntityReadTests.A_factory_timeout_does_not_let_the_caller_reuse_its_context_while_the_compute_s_query_runs` (hard and soft). Background completion can't happen while both timeouts are infinite, so `AllowTimedOutFactoryBackgroundCompletion = false` is a backstop with no test of its own. The eager-refresh pin has no behavioural test: it needs wall-clock time to pass.

## SLUICE-CORE-004 An entry never outlives the tag data that can invalidate it

**Rule:** at each level, the entry's physical lifetime, computed with FusionCache's own formula, is at most the same figure for `TagsDefaultEntryOptions`:
- memory: `MemoryCacheDuration ?? Duration`, or `FailSafeMaxDuration` when fail-safe is on and that is longer, plus the entry's `JitterMaxDuration` (FusionCache jitters memory expiry only; jitter on tag data only lengthens it, so it isn't counted);
- L2: `DistributedCacheDuration ?? Duration`, or `DistributedCacheFailSafeMaxDuration ?? FailSafeMaxDuration` when fail-safe is on and that is longer.

The cache defaults are checked when `SluiceCache` is first resolved, and a `Query` with its own `Duration` is checked on every use.
**Why:** if tag data expires first, the tag reads as "never invalidated": a fresh node serves an invalidated L2 entry, and in memory-only setups the same node serves an invalidated memory entry.
**Accepts:** entry lifetimes count from when the entry is stored and tag data's from the invalidation, so equal lifetimes leave a gap of up to one compute's duration. This is moot under the defaults, where tag data lives far longer than entries and renews when read.
**Owner:** `EntryLifetime.EnsureWithin`, called from the `SluiceCache` constructor and `SluiceCache.OptionsFor`.
**Proof:** `FusionCacheConfigurationTests.Resolving_SluiceCache_fails_when_the_default_duration_outlives_the_tag_data`, `Resolving_SluiceCache_fails_when_the_distributed_duration_outlives_the_tag_data`, `Resolving_SluiceCache_fails_when_the_distributed_fail_safe_lifetime_outlives_the_tag_data`, `Resolving_SluiceCache_fails_when_the_memory_duration_outlives_the_tag_data`, `Resolving_SluiceCache_fails_when_the_fail_safe_lifetime_outlives_the_tag_data`, `Resolving_SluiceCache_fails_when_memory_jitter_can_push_an_entry_past_the_tag_data`, `A_query_whose_duration_outlives_the_tag_data_fails_on_first_use`.

## SLUICE-CORE-005 Sluice's keys, tags and clears are isolated from other FusionCaches

**Rule:** the Sluice FusionCache is named `sluice` and uses `WithCacheKeyPrefixByCacheName()`, applied after the user's configuration.
**Why:** a cache name alone doesn't prefix keys, so Sluice's tags and clear sentinel would hit any other FusionCache sharing the same L2.
**Owner:** `SluiceServiceCollectionExtensions.AddSluice`.
**Proof:** `FusionCacheConfigurationTests.Another_FusionCache_on_the_same_L2_is_unaffected_by_Sluice_tags_and_clear`.

## SLUICE-CORE-006 Every tag contains ':'

**Rule:** the tags read and published for a dependency are `n:k`, `n:` and `n:*`; none is ever a bare source name.
**Why:** FusionCache's clear sentinels (`*` and `!`, or `-rem` and `-exp` after `SetToSafeStrings()`) are ordinary tags. Publishing one clears every *other* node.
**Owner:** `Dependency.ReadTags` and `Dependency.InvalidationTags`.
**Proof:** `MultiNodeTests.A_source_named_like_a_clear_sentinel_clears_nothing_on_other_nodes`.

## SLUICE-CORE-007 A value is cached only with the dependencies its compute recorded

**Rule:** a compute that records no dependency, or that calls `GetOrSetAsync` itself, makes the call throw `InvalidOperationException` and stores nothing. The guard returns from the FusionCache factory with memory, L2 and backplane writes skipped, and rethrows after `GetOrSetAsync` returns. A nested call marks the outer compute, so swallowing the exception doesn't help.
**Why:** a value with no dependencies is never invalidated, and a nested hit hides the inner value's dependencies from the outer entry. Throwing from the factory instead would let fail-safe serve a stale value and hide the bug.
**Owner:** `SluiceCache.GetOrSetAsync` and `SluiceCache.ComputeAsync`.
**Proof:** `ComputeGuardTests.A_compute_that_records_no_reads_throws_and_caches_nothing`, `A_compute_that_records_no_reads_stores_nothing_another_node_can_read` (L2), `A_compute_that_records_no_reads_evicts_nothing_on_another_node` (backplane), `Calling_GetOrSetAsync_inside_a_compute_throws`, `The_zero_reads_guard_throws_and_stores_nothing_even_when_fail_safe_has_an_expired_entry`, `The_nesting_guard_throws_and_stores_nothing_even_when_fail_safe_has_an_expired_entry`.

## SLUICE-CORE-008 No recorded read is lost

**Rule:** every `SluiceReads.From` overload records every dependency passed from any task while the compute runs, and throws once the compute has returned (a fused one before its fetch runs).
**Why:** a read that silently isn't recorded leaves an entry that its data's writes can't evict.
**Owner:** `SluiceReads.From`.
**Proof:** `DependencyTrackingTests.Reads_from_concurrent_tasks_inside_one_compute_are_all_recorded`, `Recording_a_read_after_the_compute_returned_throws`.

## SLUICE-CORE-009 Different query keys never share an entry

**Rule:** the default key rule (the single-argument constructors) is `string` as is and `IFormattable` through `ToString(null, CultureInfo.InvariantCulture)`. Every other key type, every interface or abstract key type, and `DateTime`, `DateTimeOffset` and `TimeOnly`, is rejected at declaration with a message naming `format`. A `format` is used as given, and one that returns null throws. Query names can't contain `:` or start with `__fc`.
**Why:** a collision serves one key's value to another key's caller. Culture-dependent text would split or merge entries across nodes, and the three date types' invariant text drops sub-second (`TimeOnly`: sub-minute) precision. With an interface or abstract key type the runtime type decides the text, so `int 1` and `long 1` would share an entry and a `DateTime` would skip the date check. FusionCache keeps tag data in the entries' key space under `__fc:t:<tag>` (`__fc-t-<tag>` after `SetToSafeStrings()`), and treats any key under that prefix as tag data: a query named `__fc` with a key starting `t:` would never be invalidated, and key `t:*` would overwrite the clear sentinel's data.
**Accepts:** a user `IFormattable` type and every `format` are the user's contract ("different keys, different text"). Equal values that print differently (`1.5m`, `1.50m`) only split the cache. Key text is wire format: changing a `format` changes every entry key and tag. A `TagCacheKeyPrefix` that doesn't start with `__fc` (set directly, or through `SetToSafeStrings()` with a special character other than `_`) isn't caught.
**Owner:** `KeyText.Default` and `KeyText.Of`, plus the name checks in the `Query` constructor.
**Proof:** `KeyTextTests.*`, `DeclarationTests.Without_a_format_a_key_type_that_is_neither_string_nor_IFormattable_is_rejected`, `Without_a_format_interface_and_abstract_key_types_are_rejected`, `Without_a_format_DateTime_DateTimeOffset_and_TimeOnly_keys_are_rejected`, `A_format_that_returns_null_throws`, `Query_names_containing_a_colon_are_rejected`, `Query_names_starting_with_FusionCache_s_internal_prefix_are_rejected`.

## SLUICE-CORE-010 A query name is declared with one set of types

**Rule:** declaring a `Query` name with a second key or value type throws `ArgumentException`. A same-typed repeat is allowed. `Source` names aren't checked.
**Why:** two `Query`s with one name but different value types share entries, so on a multi-node setup one reads the other's value from L2 as the wrong shape without an error; with different key types, `42` and `"42"` share an entry. A `Source` name clash only over-invalidates.
**Accepts:** copies in other processes, and same-typed copies with a different `format`, aren't caught.
**Owner:** `QueryNames.Claim`, called from the `Query` constructor.
**Proof:** `DeclarationTests.Redeclaring_a_query_name_with_a_different_key_or_value_type_throws`, `A_redeclaration_names_types_by_namespace_so_same_named_types_are_told_apart`.

## SLUICE-CORE-011 A fused read records its dependencies before its fetch runs

**Rule:** `read.From(dependency, fetch)` and `read.From(dependencies, fetch)` record first, then run the fetch with the compute's token. A fetch that throws leaves the dependencies recorded and propagates unchanged.
**Why:** a compute that catches the fetch's failure and returns a fallback caches that fallback; recording only on success would leave it with no dependency to evict it but the TTL.
**Owner:** `SluiceReads.From<T>(IEnumerable<Dependency>, Func<CancellationToken, Task<T>>)`.
**Proof:** `DependencyTrackingTests.A_fallback_for_a_failed_fused_fetch_is_evicted_by_its_dependency`, `A_failed_fused_fetch_that_escapes_reaches_the_caller_unchanged_and_caches_nothing`, `A_fused_read_returns_the_fetch_result_with_the_compute_token_and_records_the_dependency`.

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

**Rule:** a model-derived dependency's key text is the key converted to its provider value through the property's value converter, then folded: `decimal` scale normalised, strings right-trimmed and upper-cased invariantly, `DateTime` (local converted to UTC) and `DateTimeOffset` (UTC) as ticks truncated to whole microseconds, `byte[]` as hex, other `IFormattable` values invariant. A composite key (only writes produce one) is a JSON array of its folded parts, in key order.
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

**Rule:** `UseSluice` adds the one interceptor of its `SluiceCache` to the context. A save with no EF transaction publishes in `SavedChanges`, when it has committed (EF's own transaction, if it used one, commits before that). A save inside an EF transaction adds its dependencies to a buffer keyed weakly by the `DbTransaction`, published when that transaction commits, rolls back or fails to commit; one disposed without either publishes nothing, and its buffer is dropped when the provider starts a new transaction on the same object. A save inside an enlisted (`Database.EnlistTransaction`) or ambient (`TransactionScope`) transaction adds its dependencies to a buffer keyed weakly by the `System.Transactions.Transaction`, published from its `TransactionCompleted` event whatever the outcome; the first save into a transaction subscribes, and a transaction that has already completed raises the event at once. Publishing is awaited, or runs inside the completion event, so `SaveChanges`, `Commit` or `Dispose` returns after it. The transaction is read from `Database.CurrentTransaction`, the enlisted transaction and the ambient one, as EF's batch executor reads it, so a connection with `Enlist=false` inside a scope (which EF autocommits) publishes at once.
**Why:** publishing before the commit lets a reader cache the old row again between the publish and the commit, and nothing evicts it until its duration ends. Contexts sharing one transaction see different EF transaction ids but the same `DbTransaction`, and only the owner sees the commit. An execution strategy that retries a transaction runs each attempt in a new transaction, so only the attempt that commits publishes; a failed attempt's buffer dies with it, or publishes if its commit failed (SLUICE-EF-008).
**Accepts:** a promoted or distributed transaction can complete after `TransactionScope.Dispose` returns, and publishes then. A `TransactionScope` without `TransactionScopeAsyncFlowOption.Enabled` doesn't flow across awaits, so after an async save Sluice can see no transaction and publish at once, before the scope completes. Contexts in one scope share a buffer because `Transaction.Current` is one object per scope; a buffer per save would publish the same dependencies in more batches, which no test tells apart. A `DbTransaction` committed outside EF isn't seen. A crash between commit and publish loses the invalidation; the entry's duration bounds it.
**Owner:** `SluiceInterceptor.Complete`, `SluiceInterceptor.AddCompleting`, the `SluiceInterceptor` transaction handlers, and `SluiceInterceptor.For` (one interceptor per cache).
**Proof:** `PublishTimingTests.Inside_an_EF_transaction_a_save_is_evicted_only_once_the_transaction_commits`, `A_transaction_disposed_without_commit_evicts_nothing_even_when_its_connection_commits_later` (both start the next transaction sync and async), `A_save_on_a_context_sharing_another_context_s_transaction_is_evicted_when_it_commits`, `A_transaction_the_execution_strategy_retries_is_evicted_when_its_retry_commits` (a failed save and a failed commit); `AmbientTransactionTests.Inside_a_TransactionScope_a_save_is_evicted_only_once_the_scope_completes_whatever_its_outcome`, `Inside_an_enlisted_transaction_a_save_is_evicted_only_once_it_completes_whatever_its_outcome`, `Contexts_saving_inside_one_TransactionScope_are_all_evicted_when_it_completes`, `With_Enlist_false_a_save_inside_a_TransactionScope_is_evicted_at_once_and_outlives_the_scope`; `WriteDerivationTests.Saving_an_entity_evicts_readers_of_its_key_and_only_those`.

## SLUICE-EF-008 Every save outcome that might have committed something publishes

**Rule:** a save that fails (`SaveChangesFailed`), is cancelled (`SaveChangesCanceled`) or hits a concurrency conflict (`ThrowingConcurrencyException`, which EF reports instead of `SaveChangesFailed`) publishes what it captured through the same transaction rule as a successful save. A row whose key is temporary again (EF discards a failed save's generated keys) invalidates its whole entity source. A rolled-back transaction and a failed commit publish their buffer.
**Why:** without a transaction, or under `AutoTransactionBehavior.Never`, earlier batches commit before a later one fails, and a failed commit's outcome is unknown. Over-invalidating is safe; a dropped invalidation leaves stale values until their duration ends.
**Accepts:** a failed save over-invalidates whatever it captured, including rows that never committed, and a failed insert evicts every reader of its type.
**Owner:** `SluiceInterceptor` (`SaveChangesFailed`, `SaveChangesCanceled`, `ThrowingConcurrencyException` and the rollback and failure handlers) and `CapturedSave.Dependencies`.
**Proof:** `PublishTimingTests.A_save_that_fails_after_an_earlier_batch_committed_evicts_that_batch`, `A_concurrency_conflict_after_an_earlier_batch_committed_evicts_that_batch` (both sync and async), `A_save_cancelled_after_an_earlier_batch_committed_evicts_that_batch` (async only: a sync save takes no token, so the sync cancel handler has no test), `A_row_inserted_by_a_committed_batch_of_a_failed_save_evicts_readers_of_its_type`, `A_rolled_back_transaction_still_evicts_what_its_saves_wrote`, `A_commit_that_fails_still_evicts_what_its_saves_wrote`.

## SLUICE-EF-009 Publishing after a commit never throws and can't be cancelled

**Rule:** every publish catches any exception and logs it at `Error` through `ILogger<SluiceInterceptor>`, resolved from the services passed to `UseSluice` (which throws if logging isn't registered); the save, commit or transaction completion returns normally. Publishing goes through the core's token-less `InvalidateAsync` and `Invalidate`, and ignores the token EF passes the interceptor.
**Why:** a throw after the commit makes callers retry a write that already committed; one from a transaction's completion event surfaces in whatever completed it, on its thread. A request cancelled just after its commit must still invalidate, or the old value stays cached until its duration ends.
**Accepts:** a failed publish leaves stale values until their duration ends, visible only in the log. A slow distributed cache delays `SaveChanges`; FusionCache's `DistributedCacheHardTimeout` or circuit breaker bounds it. Inside a transaction the dependencies are derived at `SavedChanges`, before the commit, where a failure still throws.
**Owner:** `SluiceInterceptor.Publish` and `SluiceInterceptor.PublishAsync`.
**Proof:** `PublishTimingTests.A_failure_to_invalidate_after_commit_is_logged_and_the_write_returns_normally` (async save, sync save, commit and a completing `TransactionScope`), `Cancelling_the_request_just_after_its_save_committed_still_evicts`, `Cancelling_the_request_just_after_its_transaction_committed_still_evicts`.

## SLUICE-EF-010 Sync EF calls publish synchronously

**Rule:** sync `SaveChanges`, `Commit`, `Rollback` and their failure paths publish through `SluiceCache.Invalidate`, never by blocking on the async path. So does an enlisted or ambient transaction's `TransactionCompleted` event, which is synchronous and can run on another thread.
**Why:** sync-over-async can deadlock under a synchronization context and ties up a thread-pool thread per call.
**Owner:** `SluiceInterceptor.Publish`.
**Proof:** `PublishTimingTests.Sync_SaveChanges_and_sync_Commit_evict_without_going_through_async_code`, `AmbientTransactionTests.A_completing_TransactionScope_evicts_without_going_through_async_code` (their distributed cache throws on any async call, which FusionCache logs as a warning).

## SLUICE-EF-011 Capturing a save doesn't change what EF saves

**Rule:** the capture reads `ChangeTracker.Entries()`, which detects changes only when `AutoDetectChangesEnabled` is on, as `SaveChanges` itself does. It calls `ChangeTracker.CascadeChanges()` first only when a cascade timing is `OnSaveChanges` and neither is `Never`.
**Why:** an unconditional `DetectChanges()` makes EF save edits the app chose not to detect. The public `CascadeChanges()` forces every cascade, including orphan deletes the app set to `Never`.
**Accepts:** with one timing `OnSaveChanges` and the other `Never`, the cascades EF applies during the save aren't captured, so point readers of the rows it cascades aren't evicted; `Children` readers still are (SLUICE-EF-012).
**Owner:** `CapturedSave.Capture`.
**Proof:** `WriteDerivationTests.With_change_detection_off_an_undetected_edit_is_neither_saved_nor_invalidated`, `With_orphan_deletion_off_Sluice_does_not_make_EF_delete_an_orphan`, `With_cascades_on_save_deleting_a_principal_evicts_readers_of_its_tracked_dependents`.

## SLUICE-EF-012 A save invalidates real keys and its foreign keys before and after

**Rule:** foreign key values before the save are captured in `SavingChanges`; keys and current values are read from the held entries after it. Each changed row invalidates its key and the old and new values of each foreign key, skipping nulls, so a foreign key set to or from null invalidates the one principal it had; a many-to-many join row is a changed row like any other. A deleted row also invalidates every foreign key that references it, keyed by that foreign key's principal key (an alternate key included). A write to a type the read helpers refuse still publishes and doesn't fail the rest of the save.
**Why:** EF overwrites original values when it accepts the save, so a re-parented row's old parent would be missed; generated keys are temporary until the save runs; the database's `CASCADE` and `SET NULL` change rows the tracker never loaded.
**Accepts:** point readers of rows the database cascades without the tracker loading them aren't evicted; after such a delete, invalidate with `set.AllDependencies()`.
**Owner:** `CapturedSave`.
**Proof:** `WriteDerivationTests.Re_parenting_a_child_evicts_Children_readers_of_both_parents`, `Re_parenting_through_a_navigation_or_a_shadow_foreign_key_evicts_every_Children_form_of_both_parents`, `Inserting_rows_evicts_earlier_not_found_reads_of_their_generated_keys`, `Deleting_a_principal_evicts_Children_readers_of_rows_the_database_deletes_or_sets_to_null`, `Adding_editing_or_deleting_a_child_evicts_Children_readers_of_its_parent_only`, `Setting_a_nullable_foreign_key_to_or_from_null_evicts_Children_readers_of_its_principal`, `A_foreign_key_to_an_alternate_key_evicts_Children_readers_of_that_key`, `Linking_or_unlinking_many_to_many_rows_evicts_Children_readers_of_the_join_rows_on_both_sides`, `A_save_that_also_writes_a_type_reads_refuse_still_evicts_the_rest`.

## SLUICE-EF-013 A changed owned row invalidates its owner as a modified row

**Rule:** an owned entry that is Added, Modified or Deleted is captured as its owner, found among the tracked entries by the ownership's key and walked up through owned owners to the first non-owned one, in state Modified: the owner's key and its foreign keys before and after. An owner that changed itself is captured in its own right. Nothing is derived from the owned entry's own type, which no read helper can read.
**Why:** EF tracks an owned value as its own entry and leaves the owner Unchanged, but readers only see owned values through the owner: its `Entity` and `All` readers, and `Children` readers of the owner's foreign keys.
**Accepts:** each changed owned row scans the tracked entries for its owner, so a save that changes many owned rows on a large tracker pays for both.
**Owner:** `CapturedSave.Capture` (with `CapturedSave.OwnerOf`).
**Proof:** `WriteDerivationTests.Changing_an_owned_value_evicts_readers_of_its_owner_though_the_tracker_leaves_the_owner_unchanged` (a value owned by an owned value, and owned collection rows added and removed; it asserts the owner stays Unchanged).

## SLUICE-EF-014 Pooled contexts and context factories publish every lease's writes, and only those

**Rule:** `UseSluice` adds the one interceptor of the `SluiceCache` in the services it's given, so options built once (`AddDbContextPool((sp, o) => …)`, `AddPooledDbContextFactory`, `AddDbContextFactory`) carry it for their lifetime. The interceptor keeps no state per lease: a save in progress is held weakly by its `DbContext` and replaced by that context's next save, and pending dependencies are held by the transaction they wait for.
**Why:** a pooled instance serves one lease after another with options and interceptors fixed when the pool was built; state kept per instance would leak one lease's writes into another's publish, or lose them.
**Accepts:** a save that hit a concurrency conflict keeps its capture on the instance until the next save; if that save then fails before Sluice captures it, the old capture publishes again, which only over-invalidates.
**Owner:** `SluiceInterceptor.For` and the `SluiceInterceptor` weak tables.
**Proof:** `PoolingTests.A_pooled_context_evicts_what_each_lease_commits_and_nothing_an_earlier_lease_left_uncommitted` (`AddDbContextPool` and `AddPooledDbContextFactory`; it asserts the second lease is the same instance).
