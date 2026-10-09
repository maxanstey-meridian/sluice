# Invariants

Rules a reasonable-looking change could break. Each names the one place that enforces it and the tests that fail if it stops. Test names are in `tests/Sluice.Tests`. The EF package's rules (`SLUICE-EF-`) are in `src/Sluice.EntityFrameworkCore/INVARIANTS.md`. User-facing docs: `docs/`.

## SLUICE-CORE-001 The core references no EF or data-access library

**Rule:** `Sluice` and everything it transitively references contain no `Microsoft.EntityFrameworkCore*`, `Microsoft.Data*`, `System.Data*`, `Npgsql*` or `Dapper` assembly or package.
**Why:** the core is source-agnostic; Dapper, HTTP and file users must not pull in EF.
**Owner:** `src/Sluice/Sluice.csproj` (its package references).
**Proof:** `CoreReferenceTests.The_core_assembly_references_no_EF_or_data_access_library`, `The_core_package_graph_contains_no_EF_or_data_access_package` (the compiler drops unused references from metadata, so only the package graph sees an unused one).

## SLUICE-CORE-002 Invalidation removes entries; it never leaves them as fail-safe fallbacks

**Rule:** the Sluice FusionCache runs with `DisableTagging = false` and `RemoveByTagBehavior.Remove`, both applied after the user's configuration, and `ClearAsync` calls `ClearAsync(allowFailSafe: false)`.
**Why:** under `Expire`, fail-safe and soft timeouts serve a just-invalidated value when the compute fails or is slow; with tagging disabled, every invalidation throws.
**Accepts:** an invalidated entry is gone even when the data source is down, so the caller gets the error, not stale data. FusionCache 2.9.0 writes the timestamp it computes for a tag it has never checked before unconditionally, so an invalidation of that tag landing between the check's re-read and its write can be overwritten, and the invalidated entry served; the window is microseconds wide and only opens on a tag's first check.
**Owner:** `SluiceServiceCollectionExtensions.AddSluice` and `SluiceCache.ClearAsync`.
**Proof:** `FusionCacheConfigurationTests.With_fail_safe_on_a_failing_compute_after_invalidation_throws_instead_of_serving_the_invalidated_value` (its configuration sets `Expire`, so Sluice's override must win), `With_fail_safe_on_a_failing_compute_after_ClearAsync_throws_instead_of_serving_the_cleared_value`, `A_user_who_disables_tagging_still_gets_invalidation`.

## SLUICE-CORE-003 A compute finishes before its caller resumes, and its entry is stamped when it started

**Rule:** every `GetOrSetAsync` passes entry options with infinite `FactorySoftTimeout` and `FactoryHardTimeout`, `AllowTimedOutFactoryBackgroundCompletion = false` and `EagerRefreshThreshold = null`, whatever the user configured, and passes FusionCache no cancellation token: the caller's token goes to the compute and its fused fetches. An already-cancelled token throws before the cache is read.
**Why:** a timed-out factory keeps using the caller's scoped resources after the caller moved on; background completion and eager refresh stamp the entry at completion, so a mid-compute invalidation is ignored; and FusionCache throws on a cancelled token without awaiting its factory.
**Accepts:** a slow data source makes the caller wait. Cancelling doesn't interrupt a wait on another caller's compute or a slow L2 read; `LockTimeout` and the L2 timeouts bound those.
**Owner:** the `SluiceCache` constructor (`_entryOptions`) and `SluiceCache.GetOrSetAsync` (the token).
**Proof:** `FusionCacheConfigurationTests.A_compute_outliving_the_factory_timeout_completes_before_the_caller_resumes` (hard and soft), `A_cancelled_caller_resumes_only_after_its_compute_ends`, `An_already_cancelled_caller_gets_neither_a_hit_nor_a_compute`; through EF, `EntityReadTests.A_factory_timeout_does_not_let_the_caller_reuse_its_context_while_the_compute_s_query_runs`. The background-completion pin can't take effect while both timeouts are infinite, and the eager-refresh pin needs wall-clock time, so neither has a test of its own.

## SLUICE-CORE-004 An entry never outlives the tag data that can invalidate it

**Rule:** at each level the entry's physical lifetime, by FusionCache's own formula, is at most the same figure for `TagsDefaultEntryOptions`: in memory `MemoryCacheDuration ?? Duration`, or `FailSafeMaxDuration` when fail-safe is on and longer, plus the entry's `JitterMaxDuration`; in L2 `DistributedCacheDuration ?? Duration`, or `DistributedCacheFailSafeMaxDuration ?? FailSafeMaxDuration` likewise. The cache defaults are checked when `SluiceCache` is first resolved, and a `Query` with its own `Duration` on every use.
**Why:** once tag data expires the tag reads as never invalidated, so a fresh node serves an invalidated L2 entry, or a memory-only node its own invalidated entry.
**Accepts:** entry lifetimes count from the store and tag data's from the invalidation, so equal lifetimes leave a gap of up to one compute; moot under the defaults.
**Owner:** `EntryLifetime.EnsureWithin`, called from the `SluiceCache` constructor and `SluiceCache.OptionsFor`.
**Proof:** `FusionCacheConfigurationTests.Resolving_SluiceCache_fails_when_an_entry_can_outlive_the_tag_data` (default, distributed and memory durations, both fail-safe lifetimes, memory jitter), `A_query_whose_duration_outlives_the_tag_data_fails_on_first_use`.

## SLUICE-CORE-005 Sluice's keys, tags and clears are isolated from other FusionCaches

**Rule:** the Sluice FusionCache is named `sluice` and uses `WithCacheKeyPrefixByCacheName()`, applied after the user's configuration.
**Why:** a cache name alone doesn't prefix keys, so Sluice's tags and clear would hit any other FusionCache on the same L2.
**Owner:** `SluiceServiceCollectionExtensions.AddSluice`.
**Proof:** `FusionCacheConfigurationTests.Another_FusionCache_on_the_same_L2_is_unaffected_by_Sluice_tags_and_clear` (also when the user turns the prefix off).

## SLUICE-CORE-006 Every tag contains ':'

**Rule:** the tags read and published for a dependency are `n:k`, `n:` and `n:*`; none is a bare source name.
**Why:** FusionCache's clear sentinels (`*`, `!`, or `-rem` and `-exp` after `SetToSafeStrings()`) are ordinary tags, and publishing one clears every other node.
**Accepts:** key text `""` gives the tag `n:`, so invalidating it evicts every reader of the source; that only over-invalidates.
**Owner:** `Dependency.ReadTags` and `Dependency.InvalidationTags`.
**Proof:** `MultiNodeTests.A_source_named_like_a_clear_sentinel_clears_nothing_on_other_nodes`.

## SLUICE-CORE-007 A value is cached only with the dependencies its compute recorded

**Rule:** a compute that records no dependency, calls `GetOrSetAsync` itself (a fused fetch included), or uses another running compute's `read` (`From`, fused or not, or `ActiveCompute.Hold`), makes the call throw `InvalidOperationException` and stores nothing. The guard returns from the factory with memory, L2 and backplane writes skipped and rethrows after `GetOrSetAsync` returns; swallowing it in the compute doesn't help. The EF package marks the compute the same way through the hidden public `Sluice.Infrastructure.ActiveCompute` (`Reads`, `InFetch`, `QueryName`, `Violate`), held by the public API files like any other member.
**Why:** a value with no dependencies is never invalidated, a nested hit hides the inner value's dependencies, and another compute's `read` records on that compute what this one read. Throwing from the factory would let fail-safe serve a stale value and hide the bug.
**Accepts:** work started under `ExecutionContext.SuppressFlow` doesn't see the compute, so every guard is off there.
**Owner:** `SluiceCache.GetOrSetAsync` and `SluiceCache.ComputeAsync`; for another compute's `read`, `SluiceReads.ThrowIfAnotherCompute`, called from `SluiceReads.Record` and `SluiceReads.Hold`.
**Proof:** `ComputeGuardTests.A_compute_that_records_no_reads_throws_and_caches_nothing`, `A_compute_that_records_no_reads_stores_nothing_another_node_can_read`, `A_compute_that_records_no_reads_evicts_nothing_on_another_node`, `Calling_GetOrSetAsync_inside_a_compute_throws`, `Calling_GetOrSetAsync_inside_a_fused_fetch_throws`, `The_zero_reads_guard_throws_and_stores_nothing_even_when_fail_safe_has_an_expired_entry`, `The_nesting_guard_throws_and_stores_nothing_even_when_fail_safe_has_an_expired_entry`, `Using_another_running_compute_s_read_throws_and_caches_nothing` (record-only, fused, and `Hold`); for a caught EF violation, the EF tests' `TrackedReadTests.A_compute_that_catches_a_violation_caches_nothing_and_its_caller_still_gets_it`.

## SLUICE-CORE-008 No recorded read is lost

**Rule:** every `SluiceReads.From` overload records every dependency passed from any task while the compute runs, and throws once the compute has returned (a fused one before its fetch runs).
**Why:** a read that isn't recorded leaves an entry its data's writes can't evict.
**Owner:** `SluiceReads.From`.
**Proof:** `DependencyTrackingTests.Reads_from_concurrent_tasks_inside_one_compute_are_all_recorded`, `Recording_a_read_after_the_compute_returned_throws`.

## SLUICE-CORE-009 Different query keys never share an entry

**Rule:** without a `format`, `string` keys are used as they are and `IFormattable` keys through `ToString(null, CultureInfo.InvariantCulture)`; any other key type, an interface or abstract key type, and `DateTime`, `DateTimeOffset` and `TimeOnly` are rejected at declaration with a message naming `format`. A `format` is used as given; one that returns null throws. A keyless `Query<TValue>`'s entry key is its name, which no keyed entry key can equal, since those all contain `:` after the name. Query names, keyed or not, can't contain `:` or start with `__fc`.
**Why:** a collision serves one key's value to another key's caller. Culture-dependent text splits or merges entries across nodes; the three date types' invariant text drops precision; with an interface key type the runtime type picks the text (`int 1` and `long 1` collide); and FusionCache treats any key under `__fc:t:` as tag data.
**Accepts:** a user `IFormattable` type and every `format` are the user's contract. Equal values that print differently (`1.5m`, `1.50m`) only split the cache. Key text is wire format: changing a `format` changes every entry key and tag. A `TagCacheKeyPrefix` that doesn't start with `__fc` (set directly, or through `SetToSafeStrings()` with a special character other than `_`) isn't caught.
**Owner:** `KeyText.Default` and `KeyText.Of`, plus the name checks in `QueryNames.Claim`.
**Proof:** `KeyTextTests.*`, `DeclarationTests.Without_a_format_a_key_type_that_is_neither_string_nor_IFormattable_is_rejected`, `Without_a_format_interface_and_abstract_key_types_are_rejected`, `Without_a_format_DateTime_DateTimeOffset_and_TimeOnly_keys_are_rejected`, `A_format_that_returns_null_throws`, `Query_names_containing_a_colon_are_rejected`, `Query_names_starting_with_FusionCache_s_internal_prefix_are_rejected`, `A_keyless_query_name_is_checked_as_a_keyed_one_is`.

## SLUICE-CORE-010 A query name is declared with one set of types

**Rule:** declaring a `Query` name with a second key or value type, or as both a keyed and a keyless query, throws `ArgumentException`; a same-typed repeat is allowed. `Source` names aren't checked.
**Why:** two same-named queries share entries, so on a multi-node setup one reads the other's value from L2 as the wrong shape without an error, or `42` and `"42"` share an entry. A `Source` clash only over-invalidates.
**Accepts:** copies in other processes, and same-typed copies with a different `format`, aren't caught.
**Owner:** `QueryNames.Claim`, called from both `Query` constructors.
**Proof:** `DeclarationTests.Redeclaring_a_query_name_with_a_different_key_or_value_type_throws`, `A_redeclaration_names_types_by_namespace_so_same_named_types_are_told_apart`, `A_keyless_and_a_keyed_query_cannot_share_a_name`.

## SLUICE-CORE-011 A fused read records its dependencies before its fetch runs

**Rule:** `read.From(dependency, fetch)` and `read.From(dependencies, fetch)` record first, then run the fetch with the compute's token. A fetch that throws leaves the dependencies recorded and propagates unchanged. An empty list throws `ArgumentException` before the fetch runs; the record-only `read.From(dependencies)` accepts one and records nothing.
**Why:** a compute that catches the failure and returns a fallback caches it, and recording only on success would leave the fallback evictable by nothing but the TTL. A fetch that declares nothing would read with the EF origin check off (SLUICE-CORE-013).
**Owner:** `SluiceReads.From<T>(IEnumerable<Dependency>, Func<CancellationToken, Task<T>>)`.
**Proof:** `DependencyTrackingTests.A_fallback_for_a_failed_fused_fetch_is_evicted_by_its_dependency`, `A_failed_fused_fetch_that_escapes_reaches_the_caller_unchanged_and_caches_nothing`, `A_fused_read_returns_the_fetch_result_with_the_compute_token_and_records_the_dependency`, `A_fused_read_of_no_dependencies_throws_before_the_fetch_runs`, `A_record_only_read_of_no_dependencies_counts_as_no_read`.

## SLUICE-CORE-013 A fused fetch is reported as a declared read, to checks of the running compute only

**Rule:** in the flow of a fused fetch, and tasks it starts, `ActiveCompute.InFetch` is true for the running compute's reads. The rest of the compute, a fetch running beside it, and a compute started from its flow are outside it; a record-only `read.From` is no fetch. What each check does there is its own rule: SLUICE-CORE-007, SLUICE-EF-002 and SLUICE-EF-015.
**Why:** the fetch's recorded dependencies cover what it reads, by declaration; it's the route for views, keyless types and raw SQL.
**Accepts:** a fetch's plain query may read rows its declaration doesn't cover, or track; that's the app's declaration. A task the fetch leaves running stays inside it until the compute ends.
**Owner:** `ActiveCompute` (`Fetch` and `InFetch`).
**Proof:** `ComputeGuardTests.Calling_GetOrSetAsync_inside_a_fused_fetch_throws`; the EF tests' `TrackedReadTests.A_query_run_in_a_fused_fetch_is_cached_and_evicted_by_its_declared_dependency`, `A_query_after_a_read_From_that_only_records_is_still_checked`, `While_fused_fetches_run_side_by_side_each_runs_queries_on_any_context_and_the_compute_s_own_flow_does_not`, `A_compute_started_from_a_fused_fetch_that_outlived_its_own_compute_is_checked`, `A_compute_that_catches_a_violation_caches_nothing_and_its_caller_still_gets_it`.

## SLUICE-CORE-014 A compute runs outside the caller's ambient transaction

**Rule:** while a compute runs, `Transaction.Current` is null: `ComputeAsync` suppresses the caller's ambient transaction around it, and the caller has it back once the compute ends.
**Why:** a cached value is served to every caller, so it mustn't see a transaction's uncommitted rows, or an old snapshot. A connection the compute opens would otherwise enlist in the caller's transaction.
**Accepts:** a connection already enlisted before the compute (one the caller opened) stays enlisted; the EF package refuses reads on it (SLUICE-EF-002). A compute that opens its own `TransactionScope` is the app's choice.
**Owner:** `SluiceCache.ComputeAsync`.
**Proof:** `ComputeScopeTests.A_compute_runs_outside_the_caller_s_ambient_transaction_and_the_caller_gets_it_back`; the EF tests' `ReadGuardTests.A_compute_reads_what_is_committed_inside_an_enlisted_transaction_or_a_TransactionScope`.

## SLUICE-CORE-015 What a compute holds lives exactly as long as the compute

**Rule:** `ActiveCompute.Hold` gives a compute one instance of each resource type, made from the application's services on first use, and the compute disposes every one, newest first, once it has returned, before its caller resumes, however it ended (a value, an exception, a cancellation, a violation, no dependencies). A dispose that throws doesn't stop the rest, and never throws out of the FusionCache factory: it's recorded as a violation, so nothing is stored even with fail-safe on and an expired entry. The caller gets a guard violation first; else the compute's exception and the dispose's together in an `AggregateException`; else the dispose's exception (an `AggregateException` for several). Holding something after the compute returned throws. `ActiveCompute.Holds` compares by reference. Both are hidden public members of `Sluice.Infrastructure.ActiveCompute`, held by the public API files.
**Why:** the EF package's `read.Db` context must be the compute's own: created inside it, recognisable as its own (`ActiveCompute.Holds`), and gone with it, so nothing it read outlives the compute. A throw from the factory would replace the compute's exception or violation, skip the no-dependencies guard, and let fail-safe serve the expired value.
**Accepts:** a task the compute leaves running may find its resource disposed.
**Owner:** `SluiceReads.Hold`, `SluiceReads.Holds` and `SluiceReads.ReleaseAsync`, called from `SluiceCache.ComputeAsync`.
**Proof:** `ComputeScopeTests.What_a_compute_holds_is_one_instance_per_type_and_is_disposed_when_it_ends`, `What_a_compute_holds_is_disposed_newest_first`, `What_a_compute_holds_is_disposed_however_the_compute_ends` (throws, is cancelled, records a violation, records no dependencies), `When_one_held_resource_s_dispose_throws_the_rest_are_still_disposed_and_its_exception_reaches_the_caller`, `When_several_held_resources_dispose_throws_all_are_disposed_and_the_caller_gets_every_exception`, `When_the_compute_throws_and_a_dispose_throws_the_caller_gets_both_and_nothing_is_cached`, `When_the_compute_records_a_violation_and_a_dispose_throws_the_caller_gets_the_violation`, `A_dispose_that_throws_stores_nothing_even_when_fail_safe_has_an_expired_entry`, `A_compute_holds_only_the_very_instance_it_was_given`; the EF tests' `ComputeContextTests.A_compute_gets_one_context_that_does_not_track_and_is_disposed_when_it_ends`.

## SLUICE-CORE-016 Sluice is registered once

**Rule:** a second `AddSluice` on one service collection throws `InvalidOperationException`, saying to call it once.
**Why:** FusionCache's own error for a second named cache doesn't say which call made it.
**Owner:** `SluiceServiceCollectionExtensions.AddSluice`.
**Proof:** `FusionCacheConfigurationTests.Registering_Sluice_twice_throws`.
