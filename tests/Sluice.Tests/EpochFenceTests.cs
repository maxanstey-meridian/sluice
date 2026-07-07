namespace Sluice.Tests;

public sealed class EpochFenceTests
{
    [Fact]
    public async Task HasOverlappingInvalidationAsync_UpperBound_ExcludesOverlapBeyondThroughEpoch()
    {
        var fence = new InMemoryEpochFence();
        var observedRead = new ResourceAddress(ResourceKind.Entity, "test", "shared");

        // Build records with known epochs:
        // Inside window (afterEpoch=1, throughEpoch=4) → epochs 2,3,4 are checked
        await fence.IncrementEpochAsync(
            [new ResourceAddress(ResourceKind.Entity, "other", "a")],
            CancellationToken.None
        );
        await fence.IncrementEpochAsync(
            [new ResourceAddress(ResourceKind.Entity, "other", "b")],
            CancellationToken.None
        );
        await fence.IncrementEpochAsync(
            [new ResourceAddress(ResourceKind.Entity, "other", "c")],
            CancellationToken.None
        );
        await fence.IncrementEpochAsync(
            [new ResourceAddress(ResourceKind.Entity, "other", "d")],
            CancellationToken.None
        );
        // Epoch 5: overlaps observedRead but is BEYOND throughEpoch=4 → must be excluded
        await fence.IncrementEpochAsync(
            [new ResourceAddress(ResourceKind.Entity, "test", "shared")],
            CancellationToken.None
        );

        var result = await fence.HasOverlappingInvalidationAsync(
            afterEpoch: 1,
            throughEpoch: 4,
            observedReads: [observedRead],
            CancellationToken.None
        );

        result
            .Should()
            .BeFalse(
                "overlapping invalidation at epoch 5 is excluded by upper bound throughEpoch=4"
            );
    }

    [Fact]
    public async Task HasOverlappingInvalidationAsync_LowerBound_ExcludesRecordsBelowOrAtAfterEpoch()
    {
        var fence = new InMemoryEpochFence();
        var observedRead = new ResourceAddress(ResourceKind.Entity, "test", "x");

        await fence.IncrementEpochAsync([observedRead], CancellationToken.None);
        await fence.IncrementEpochAsync(
            [new ResourceAddress(ResourceKind.Entity, "other", "b")],
            CancellationToken.None
        );

        var result = await fence.HasOverlappingInvalidationAsync(
            afterEpoch: 1,
            throughEpoch: 2,
            observedReads: [observedRead],
            CancellationToken.None
        );

        result
            .Should()
            .BeFalse("overlapping record at epoch 1 is excluded by lower bound afterEpoch=1");
    }

    [Fact]
    public async Task HasOverlappingInvalidationAsync_DetectsOverlapWithinWindow()
    {
        var fence = new InMemoryEpochFence();
        var observedRead = new ResourceAddress(ResourceKind.Entity, "test", "x");

        await fence.IncrementEpochAsync(
            [new ResourceAddress(ResourceKind.Entity, "other", "a")],
            CancellationToken.None
        );
        await fence.IncrementEpochAsync([observedRead], CancellationToken.None);
        await fence.IncrementEpochAsync(
            [new ResourceAddress(ResourceKind.Entity, "other", "b")],
            CancellationToken.None
        );

        var result = await fence.HasOverlappingInvalidationAsync(
            afterEpoch: 1,
            throughEpoch: 3,
            observedReads: [observedRead],
            CancellationToken.None
        );

        result.Should().BeTrue("overlapping record at epoch 2 is within window (1, 3]");
    }

    [Fact]
    public async Task HasOverlappingInvalidationAsync_GuardFiresWhenWindowExceedsMaxRecent()
    {
        var fence = new InMemoryEpochFence();

        for (int i = 0; i < 257; i++)
        {
            await fence.IncrementEpochAsync(
                [new ResourceAddress(ResourceKind.Entity, "other", i.ToString())],
                CancellationToken.None
            );
        }

        var result = await fence.HasOverlappingInvalidationAsync(
            afterEpoch: 0,
            throughEpoch: 257,
            observedReads: [new ResourceAddress(ResourceKind.Entity, "test", "x")],
            CancellationToken.None
        );

        result
            .Should()
            .BeTrue("guard fires when throughEpoch - afterEpoch >= MaxRecentInvalidations");
    }

    [Fact]
    public async Task HasOverlappingInvalidationAsync_DetectsTrimmedOverlap_WithPostScanGuard()
    {
        var fence = new InMemoryEpochFence();
        var overlappingAddress = new ResourceAddress(ResourceKind.Entity, "test", "shared");
        var otherAddress = new ResourceAddress(ResourceKind.Entity, "other", "x");

        // Fill the queue to MaxRecentInvalidations (256 entries), epochs 1-256
        for (int i = 0; i < 256; i++)
        {
            await fence.IncrementEpochAsync([otherAddress], CancellationToken.None);
        }

        var epochBefore = await fence.ReadEpochAsync(CancellationToken.None); // 256

        // Overlapping invalidation during compute at epoch 257
        await fence.IncrementEpochAsync([overlappingAddress], CancellationToken.None);

        // Add 254 more entries to widen the gap without triggering the wide-window guard.
        // throughEpoch will be 511 (256 + 1 + 254), so throughEpoch - afterEpoch = 255 < 256.
        for (int i = 0; i < 254; i++)
        {
            await fence.IncrementEpochAsync([otherAddress], CancellationToken.None);
        }

        var epochAfter = await fence.ReadEpochAsync(CancellationToken.None); // 511

        // Simulate the race: after epochAfter was read but before the snapshot inside
        // HasOverlappingInvalidationAsync, more writes advance the epoch and trim
        // the overlapping record at epoch 257 from the queue.
        await fence.IncrementEpochAsync([otherAddress], CancellationToken.None); // 512, trims epoch 256
        await fence.IncrementEpochAsync([otherAddress], CancellationToken.None); // 513, trims epoch 257

        // Now call HasOverlappingInvalidationAsync with the OLD epochAfter (511).
        // The overlapping record at epoch 257 has been trimmed from the queue.
        // The wide-window guard doesn't fire (255 < 256).
        // The snapshot scan finds no overlap (epoch 257 is gone).
        // The post-scan guard must catch it.
        var result = await fence.HasOverlappingInvalidationAsync(
            afterEpoch: epochBefore, // 256
            throughEpoch: epochAfter, // 511
            observedReads: [overlappingAddress],
            CancellationToken.None
        );

        result
            .Should()
            .BeTrue(
                "overlapping invalidation at epoch 257 was trimmed from the queue before the "
                    + "overlap check ran, but the post-scan epoch guard should detect the trim window"
            );
    }
}
