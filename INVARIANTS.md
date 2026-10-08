# Invariants

Rules a reasonable-looking change could break. Each names the one place that enforces it and the test that fails if it stops. Test names are in `tests/Sluice.Tests`.

## SLUICE-CORE-001 The core references no EF or data-access library

**Rule:** `Sluice` and everything it transitively references contain no `Microsoft.EntityFrameworkCore*`, `Microsoft.Data*`, `System.Data*`, `Npgsql*` or `Dapper` assembly. EF support lives only in `Sluice.EntityFrameworkCore`.
**Why:** the core is source-agnostic (DESIGN D1). Dapper, HTTP and file users must not pull in EF.
**Owner:** `src/Sluice/Sluice.csproj` (its package references).
**Proof:** `CoreReferenceTests.The_core_assembly_references_no_EF_or_data_access_library`.

## SLUICE-CORE-002 Invalidation removes entries; it never leaves them as fail-safe fallbacks

**Rule:** the Sluice FusionCache runs with `RemoveByTagBehavior.Remove`, applied after the user's configuration, and `ClearAsync` calls `ClearAsync(allowFailSafe: false)`.
**Why:** under FusionCache's default `Expire`, fail-safe and soft timeouts serve a value that was just invalidated whenever the compute fails or is slow.
**Accepts:** an invalidated entry is gone even when the data source is down, so the caller gets the error instead of stale data.
**Owner:** `SluiceServiceCollectionExtensions.AddSluice` (behaviour) and `SluiceCache.ClearAsync`.
**Proof:** `FusionCacheConfigurationTests.With_fail_safe_on_a_failing_compute_after_invalidation_throws_instead_of_serving_the_invalidated_value`.

## SLUICE-CORE-003 A compute finishes before its caller resumes, and its entry is stamped when it started

**Rule:** every `GetOrSetAsync` call passes entry options with `FactorySoftTimeout` and `FactoryHardTimeout` infinite, `AllowTimedOutFactoryBackgroundCompletion = false` and `EagerRefreshThreshold = null`, whatever the user configured. It passes FusionCache no cancellation token: the caller's token goes to the compute, and fused reads hand it to their fetches (DESIGN D14).
**Why:** a timed-out factory keeps using the caller's scoped resources (a `DbContext`, a connection) after the caller moved on, and background completion and eager refresh stamp the entry when the compute *finishes*, which lets an invalidation that landed mid-compute be ignored. On a cancelled token FusionCache throws without awaiting its factory, so a token passed to it would let a cancelled caller resume while its compute still runs.
**Accepts:** a slow data source makes the caller wait; FusionCache timeouts don't apply to Sluice computes. Cancelling doesn't interrupt a wait on another caller's compute (FusionCache's memory lock) or a slow L2 read; `LockTimeout` and the L2 timeouts bound those.
**Owner:** the `SluiceCache` constructor (`_entryOptions`) and `SluiceCache.GetOrSetAsync` (the token).
**Proof:** `FusionCacheConfigurationTests.A_compute_outliving_the_factory_hard_timeout_completes_before_the_caller_resumes`, `A_cancelled_caller_resumes_only_after_its_compute_ends`. The background-completion and eager-refresh pins have no behavioural test: both need wall-clock time to pass.

## SLUICE-CORE-004 An entry never outlives the tag data that can invalidate it

**Rule:** at each level, the entry's physical lifetime, computed with FusionCache's own formula, is at most the same figure for `TagsDefaultEntryOptions`:
- memory: `MemoryCacheDuration ?? Duration`, or `FailSafeMaxDuration` when fail-safe is on and that is longer (DESIGN D15);
- L2: `DistributedCacheDuration ?? Duration`, or `DistributedCacheFailSafeMaxDuration ?? FailSafeMaxDuration` when fail-safe is on and that is longer.

The cache defaults are checked when `SluiceCache` is first resolved, and a `Query` with its own `Duration` is checked on every use.
**Why:** if tag data expires first, the tag reads as "never invalidated": a fresh node serves an invalidated L2 entry, and in memory-only setups the same node serves an invalidated memory entry.
**Owner:** `EntryLifetime.EnsureWithin`, called from the `SluiceCache` constructor and `SluiceCache.OptionsFor`.
**Proof:** `FusionCacheConfigurationTests.Startup_fails_when_the_default_duration_outlives_the_tag_data`, `Startup_fails_when_the_distributed_duration_outlives_the_tag_data`, `Startup_fails_when_the_memory_duration_outlives_the_tag_data`, `Startup_fails_when_the_fail_safe_lifetime_outlives_the_tag_data`, `A_query_whose_duration_outlives_the_tag_data_fails_on_first_use`.

## SLUICE-CORE-005 Sluice's keys, tags and clears are isolated from other FusionCaches

**Rule:** the Sluice FusionCache is named `sluice` and uses `WithCacheKeyPrefixByCacheName()`, applied after the user's configuration.
**Why:** a cache name alone doesn't prefix keys, so Sluice's tags and clear sentinel would hit any other FusionCache sharing the same L2.
**Owner:** `SluiceServiceCollectionExtensions.AddSluice`.
**Proof:** `FusionCacheConfigurationTests.Another_FusionCache_on_the_same_L2_is_unaffected_by_Sluice_tags_and_clear`.

## SLUICE-CORE-006 Every tag contains ':'

**Rule:** the tags read and published for a dependency are `n:k`, `n:` and `n:*` (DESIGN §5.1); none is ever a bare source name.
**Why:** FusionCache's clear sentinels (`*` and `!`, or `-rem` and `-exp` after `SetToSafeStrings()`) are ordinary tags. Publishing one clears every *other* node.
**Owner:** `Dependency.ReadTags` and `Dependency.InvalidationTags`.
**Proof:** `MultiNodeTests.A_source_named_like_a_clear_sentinel_clears_nothing_on_other_nodes`.

## SLUICE-CORE-007 A value is cached only with the dependencies its compute recorded

**Rule:** a compute that records no dependency, or that calls `GetOrSetAsync` itself, makes the call throw `InvalidOperationException` and stores nothing. The guard returns from the FusionCache factory with memory, L2 and backplane writes skipped, and rethrows after `GetOrSetAsync` returns. A nested call marks the outer compute, so swallowing the exception doesn't help.
**Why:** a value with no dependencies is never invalidated, and a nested hit hides the inner value's dependencies from the outer entry. Throwing from the factory instead would let fail-safe serve a stale value and hide the bug.
**Owner:** `SluiceCache.GetOrSetAsync` and `SluiceCache.ComputeAsync`.
**Proof:** `ComputeGuardTests.A_compute_that_records_no_reads_throws_and_caches_nothing`, `Calling_GetOrSetAsync_inside_a_compute_throws`, `The_zero_reads_guard_throws_and_stores_nothing_even_when_fail_safe_has_an_expired_entry`, `The_nesting_guard_throws_and_stores_nothing_even_when_fail_safe_has_an_expired_entry`.

## SLUICE-CORE-008 No recorded read is lost

**Rule:** every `SluiceReads.From` overload records every dependency passed from any task while the compute runs, and throws once the compute has returned (a fused one before its fetch runs).
**Why:** a read that silently isn't recorded leaves an entry that its data's writes can't evict.
**Owner:** `SluiceReads.From`.
**Proof:** `DependencyTrackingTests.Reads_from_concurrent_tasks_inside_one_compute_are_all_recorded`, `Recording_a_read_after_the_compute_returned_throws`.

## SLUICE-CORE-009 Different query keys never share an entry

**Rule:** the default key rule (the single-argument constructors) is `string` as is and `IFormattable` through `ToString(null, CultureInfo.InvariantCulture)`. Every other key type, and `DateTime`, `DateTimeOffset` and `TimeOnly`, is rejected at declaration with a message naming `format`. A `format` is used as given. Query names can't contain `:`.
**Why:** a collision serves one key's value to another key's caller. Culture-dependent text would split or merge entries across nodes, and the three date types' invariant text drops sub-second (`TimeOnly`: sub-minute) precision.
**Accepts:** a user `IFormattable` type and every `format` are the user's contract ("different keys, different text"). Equal values that print differently (`1.5m`, `1.50m`) only split the cache. Key text is wire format: changing a `format` changes every entry key and tag.
**Owner:** `KeyText.Default`, plus the name check in the `Query` constructor.
**Proof:** `KeyTextTests.*`, `DeclarationTests.Without_a_format_a_key_type_that_is_neither_string_nor_IFormattable_is_rejected`, `Without_a_format_DateTime_DateTimeOffset_and_TimeOnly_keys_are_rejected`, `Blank_query_names_and_names_containing_a_colon_are_rejected`.

## SLUICE-CORE-010 A query name is declared with one set of types

**Rule:** declaring a `Query` name with a second key or value type throws `ArgumentException`. A same-typed repeat is allowed. `Source` names aren't checked (DESIGN D13).
**Why:** two `Query`s with one name but different value types share entries, so on a multi-node setup one reads the other's value from L2 as the wrong shape without an error; with different key types, `42` and `"42"` share an entry. A `Source` name clash only over-invalidates.
**Accepts:** copies in other processes, and same-typed copies with a different `format`, aren't caught.
**Owner:** `Declarations.Claim`, called from the `Query` constructor.
**Proof:** `DeclarationTests.Redeclaring_a_query_name_with_a_different_key_or_value_type_throws`.

## SLUICE-CORE-011 A fused read records its dependencies before its fetch runs

**Rule:** `read.From(dependency, fetch)` and `read.From(dependencies, fetch)` record first, then run the fetch with the compute's token. A fetch that throws leaves the dependencies recorded and propagates unchanged.
**Why:** a compute that catches the fetch's failure and returns a fallback caches that fallback; recording only on success would leave it with no dependency to evict it but the TTL.
**Owner:** `SluiceReads.From<T>(IEnumerable<Dependency>, Func<CancellationToken, Task<T>>)`.
**Proof:** `DependencyTrackingTests.A_fallback_for_a_failed_fused_fetch_is_evicted_by_its_dependency`, `A_failed_fused_fetch_that_escapes_reaches_the_caller_unchanged_and_caches_nothing`, `A_fused_read_returns_the_fetch_result_with_the_compute_token_and_records_the_dependency`.
