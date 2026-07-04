// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Consensus.Test.Processing;

/// <summary>
/// Pins <see cref="BalStateRootShadow"/>: it must recompute the post-block state root from the BAL off the
/// processing thread, compare it non-blockingly, and count matches/mismatches/errors/skips without ever
/// blocking the caller or affecting consensus.
/// </summary>
[TestFixture]
public class BalStateRootShadowTests
{
    private const int PollTimeoutMs = 5000;

    private static IBalStateRootConfig Enabled() => new BalStateRootConfig { Enabled = true };
    private static IBalStateRootConfig Disabled() => new BalStateRootConfig { Enabled = false };

    private static ITrieStore NewStore() => TestTrieStoreFactory.Build(new MemDb(), LimboLogs.Instance);

    /// <summary>A factory that hands the shadow a fresh <see cref="IReadOnlyTrieStore"/> wrapper each call.</summary>
    private static Func<IReadOnlyTrieStore> Factory(ITrieStore inner) => () => new NonDisposingReadOnlyStore(inner);

    /// <summary>
    /// Builds a committed single-account pre-state over <paramref name="store"/>, returns the parent header
    /// (pre-state root) and a block whose header state root is the correct post-state root for a balance change,
    /// paired with the matching BAL. The BAL and header agree, so an honest recompute must match.
    /// </summary>
    private static (BlockHeader parent, Block block) BuildMatchingBlock(ITrieStore store, UInt256 postBalance)
    {
        Address address = TestItem.AddressA;

        StateTree preTree = new(store.GetTrieStore(null), LimboLogs.Instance);
        preTree.Set(address, new Account(1, 100));
        preTree.Commit();
        Hash256 preRoot = preTree.RootHash;

        StateTree postTree = new(store.GetTrieStore(null), LimboLogs.Instance);
        postTree.SetRootHash(preRoot, true);
        postTree.Set(address, new Account(1, postBalance));
        postTree.UpdateRootHash(canBeParallel: false);
        Hash256 postRoot = postTree.RootHash;

        ReadOnlyBlockAccessList bal = Build.A.BlockAccessList
            .WithAccountChanges(Build.An.AccountChanges
                .WithAddress(address)
                .WithBalanceChanges(new BalanceChange(0, postBalance))
                .TestObject)
            .TestObject;

        BlockHeader parent = Build.A.BlockHeader.WithStateRoot(preRoot).TestObject;
        Block block = Build.A.Block
            .WithNumber(1)
            .WithStateRoot(postRoot)
            .WithBlockAccessList(bal)
            .TestObject;

        return (parent, block);
    }

    /// <summary>A block with a BAL but backed by no real state, so the shadow computation always throws.</summary>
    private static (BlockHeader parent, Block block) ErroringBlock()
    {
        ReadOnlyBlockAccessList bal = Build.A.BlockAccessList
            .WithAccountChanges(Build.An.AccountChanges
                .WithAddress(TestItem.AddressA)
                .WithBalanceChanges(new BalanceChange(0, (UInt256)1))
                .TestObject)
            .TestObject;

        // Parent root points at a state that does not exist in the store: the explicit-root Get throws.
        BlockHeader parent = Build.A.BlockHeader.WithStateRoot(TestItem.KeccakA).TestObject;
        Block block = Build.A.Block.WithNumber(1).WithStateRoot(TestItem.KeccakB).WithBlockAccessList(bal).TestObject;
        return (parent, block);
    }

    private static void PollUntil(Func<bool> condition)
    {
        SpinWait spin = default;
        long deadline = Environment.TickCount64 + PollTimeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline) Assert.Fail("Condition not met within timeout.");
            spin.SpinOnce();
        }
    }

    [Test]
    public void T3_1_disabled_returns_completed_null_task_and_does_no_work()
    {
        using ITrieStore store = NewStore();
        (BlockHeader parent, Block block) = BuildMatchingBlock(store, 500);
        BalStateRootShadow shadow = new(Factory(store), Disabled(), LimboLogs.Instance);

        Task<Hash256?> lane = shadow.Start(parent, block);

        Assert.That(lane.IsCompletedSuccessfully, Is.True);
        Assert.That(lane.Result, Is.Null);
    }

    [Test]
    public void T3_2_block_without_bal_returns_null_task()
    {
        using ITrieStore store = NewStore();
        BlockHeader parent = Build.A.BlockHeader.WithStateRoot(TestItem.KeccakA).TestObject;
        Block block = Build.A.Block.WithNumber(1).TestObject; // no BAL
        BalStateRootShadow shadow = new(Factory(store), Enabled(), LimboLogs.Instance);

        Task<Hash256?> lane = shadow.Start(parent, block);

        Assert.That(lane.IsCompletedSuccessfully, Is.True);
        Assert.That(lane.Result, Is.Null);
    }

    [Test]
    public void T3_3_matching_block_records_a_match()
    {
        long matchesBefore = Blockchain.Metrics.BalShadowRootMatches;
        long mismatchesBefore = Blockchain.Metrics.BalShadowRootMismatches;

        using ITrieStore store = NewStore();
        (BlockHeader parent, Block block) = BuildMatchingBlock(store, 777);
        BalStateRootShadow shadow = new(Factory(store), Enabled(), LimboLogs.Instance);

        Task<Hash256?> lane = shadow.Start(parent, block);
        shadow.Compare(lane, block);

        PollUntil(() => Blockchain.Metrics.BalShadowRootMatches > matchesBefore);
        Assert.That(Blockchain.Metrics.BalShadowRootMismatches, Is.EqualTo(mismatchesBefore));
    }

    [Test]
    public void T3_4_corrupted_header_root_records_a_mismatch()
    {
        long mismatchesBefore = Blockchain.Metrics.BalShadowRootMismatches;

        using ITrieStore store = NewStore();
        (BlockHeader parent, Block block) = BuildMatchingBlock(store, 777);
        // Corrupt the header state root so the honest recompute disagrees.
        block.Header.StateRoot = TestItem.KeccakF;
        BalStateRootShadow shadow = new(Factory(store), Enabled(), LimboLogs.Instance);

        Task<Hash256?> lane = shadow.Start(parent, block);
        shadow.Compare(lane, block);

        PollUntil(() => Blockchain.Metrics.BalShadowRootMismatches > mismatchesBefore);
    }

    [Test]
    public void T3_5_slow_calculator_does_not_block_start_or_compare()
    {
        long matchesBefore = Blockchain.Metrics.BalShadowRootMatches;

        using ITrieStore inner = NewStore();
        (BlockHeader parent, Block block) = BuildMatchingBlock(inner, 999);

        using ManualResetEventSlim gate = new(false);
        BalStateRootShadow shadow = new(() => new SlowReadOnlyStore(inner, gate), Enabled(), LimboLogs.Instance);

        long startTicks = Environment.TickCount64;
        Task<Hash256?> lane = shadow.Start(parent, block);
        shadow.Compare(lane, block);
        long elapsed = Environment.TickCount64 - startTicks;

        // The calculator is blocked; Start/Compare must have returned without waiting on it.
        Assert.That(elapsed, Is.LessThan(1000), "Start/Compare blocked on the slow calculator.");
        Assert.That(Blockchain.Metrics.BalShadowRootMatches, Is.EqualTo(matchesBefore));

        // Release the calculator; the comparison must still be recorded.
        gate.Set();
        PollUntil(() => Blockchain.Metrics.BalShadowRootMatches > matchesBefore);
    }

    [Test]
    public void T3_6_scope_required_store_still_computes()
    {
        long matchesBefore = Blockchain.Metrics.BalShadowRootMatches;
        long errorsBefore = Blockchain.Metrics.BalShadowRootErrors;

        using ITrieStore inner = NewStore();
        (BlockHeader parent, Block block) = BuildMatchingBlock(inner, 4242);
        // Reads throw unless BeginScope was called first (the flat-store contract).
        BalStateRootShadow shadow = new(() => new ScopeRequiredReadOnlyStore(inner), Enabled(), LimboLogs.Instance);

        Task<Hash256?> lane = shadow.Start(parent, block);
        shadow.Compare(lane, block);

        PollUntil(() => Blockchain.Metrics.BalShadowRootMatches > matchesBefore
                        || Blockchain.Metrics.BalShadowRootErrors > errorsBefore);
        Assert.That(Blockchain.Metrics.BalShadowRootErrors, Is.EqualTo(errorsBefore), "ComputeRoot errored despite BeginScope being called.");
        Assert.That(Blockchain.Metrics.BalShadowRootMatches, Is.GreaterThan(matchesBefore));
    }

    [Test]
    public void T3_7_in_flight_cap_skips_the_fifth_lane()
    {
        long skippedBefore = Blockchain.Metrics.BalShadowRootSkipped;

        using ITrieStore inner = NewStore();
        (BlockHeader parent, Block block) = BuildMatchingBlock(inner, 555);

        using ManualResetEventSlim gate = new(false);
        BalStateRootShadow shadow = new(() => new SlowReadOnlyStore(inner, gate), Enabled(), LimboLogs.Instance);

        // Fill the 4-slot in-flight cap with lanes blocked on the gate.
        for (int i = 0; i < 4; i++)
        {
            Assert.That(shadow.Start(parent, block).IsCompleted, Is.False, "in-flight lane should be pending");
        }

        // The 5th Start is over cap: completed null task, and Skipped incremented.
        Task<Hash256?> fifth = shadow.Start(parent, block);
        Assert.That(fifth.IsCompletedSuccessfully, Is.True);
        Assert.That(fifth.Result, Is.Null);
        Assert.That(Blockchain.Metrics.BalShadowRootSkipped, Is.EqualTo(skippedBefore + 1));

        gate.Set();
        Assert.That(shadow.WaitForIdle(TimeSpan.FromSeconds(PollTimeoutMs / 1000.0)), Is.True);
    }

    [Test]
    public void T3_8_slot_released_without_compare_then_lane_is_reusable()
    {
        using ITrieStore inner = NewStore();
        (BlockHeader parent, Block block) = BuildMatchingBlock(inner, 321);
        BalStateRootShadow shadow = new(Factory(inner), Enabled(), LimboLogs.Instance);

        // Dispatch 4 lanes and NEVER call Compare (the rejected-block path). Slots must still be freed.
        for (int i = 0; i < 4; i++) shadow.Start(parent, block);
        Assert.That(shadow.WaitForIdle(TimeSpan.FromSeconds(PollTimeoutMs / 1000.0)), Is.True, "in-flight slots not freed without Compare");

        // The lane is reusable afterwards: a fresh Start dispatches real work rather than skipping.
        long matchesBefore = Blockchain.Metrics.BalShadowRootMatches;
        Task<Hash256?> lane = shadow.Start(parent, block);
        Assert.That(lane.IsCompleted, Is.False, "expected a dispatched lane, got the skip sentinel");
        shadow.Compare(lane, block);
        PollUntil(() => Blockchain.Metrics.BalShadowRootMatches > matchesBefore);
    }

    [Test]
    public void T3_9_consecutive_errors_self_disable_then_start_returns_null()
    {
        using ITrieStore inner = NewStore();
        (BlockHeader parent, Block block) = ErroringBlock();
        BalStateRootShadow shadow = new(Factory(inner), Enabled(), LimboLogs.Instance);

        // Drive 5 consecutive computation errors, one lane at a time (so consecutive count is deterministic).
        for (int i = 0; i < 5; i++)
        {
            Task<Hash256?> lane = shadow.Start(parent, block);
            Assert.That(lane.IsCompleted, Is.False, $"lane {i} should have dispatched real work");
            Assert.That(shadow.WaitForIdle(TimeSpan.FromSeconds(PollTimeoutMs / 1000.0)), Is.True);
        }

        // After the 5th consecutive error the lane self-disables: Start returns the completed null sentinel.
        Task<Hash256?> after = shadow.Start(parent, block);
        Assert.That(after.IsCompletedSuccessfully, Is.True);
        Assert.That(after.Result, Is.Null);
    }

    [Test]
    public void T3_10_success_between_errors_resets_the_consecutive_counter()
    {
        using ITrieStore inner = NewStore();
        (BlockHeader goodParent, Block goodBlock) = BuildMatchingBlock(inner, 888);
        (BlockHeader badParent, Block badBlock) = ErroringBlock();
        BalStateRootShadow shadow = new(Factory(inner), Enabled(), LimboLogs.Instance);

        // 4 errors (below the threshold of 5), then a success resets the counter, then 4 more errors.
        // Without the reset, the 5th cumulative error would self-disable; with it, the lane stays enabled.
        for (int round = 0; round < 4; round++)
        {
            shadow.Start(badParent, badBlock);
            Assert.That(shadow.WaitForIdle(TimeSpan.FromSeconds(PollTimeoutMs / 1000.0)), Is.True);
        }

        shadow.Start(goodParent, goodBlock);
        Assert.That(shadow.WaitForIdle(TimeSpan.FromSeconds(PollTimeoutMs / 1000.0)), Is.True);

        for (int round = 0; round < 4; round++)
        {
            shadow.Start(badParent, badBlock);
            Assert.That(shadow.WaitForIdle(TimeSpan.FromSeconds(PollTimeoutMs / 1000.0)), Is.True);
        }

        // Still enabled: a fresh Start dispatches real work rather than returning the skip sentinel.
        Task<Hash256?> lane = shadow.Start(goodParent, goodBlock);
        Assert.That(lane.IsCompleted, Is.False, "lane self-disabled despite a success resetting the error run");
        Assert.That(shadow.WaitForIdle(TimeSpan.FromSeconds(PollTimeoutMs / 1000.0)), Is.True);
    }

    /// <summary>Delegating read-only store whose <see cref="BeginScope"/> blocks on a gate, to simulate a slow calculator.</summary>
    private sealed class SlowReadOnlyStore(ITrieStore inner, ManualResetEventSlim gate) : DelegatingReadOnlyTrieStore(inner)
    {
        public override IDisposable BeginScope(BlockHeader? baseBlock)
        {
            gate.Wait();
            return base.BeginScope(baseBlock);
        }
    }

    /// <summary>Delegating read-only store whose read/scope surface throws unless <see cref="BeginScope"/> ran first.</summary>
    private sealed class ScopeRequiredReadOnlyStore(ITrieStore inner) : DelegatingReadOnlyTrieStore(inner)
    {
        private bool _scoped;

        public override IDisposable BeginScope(BlockHeader? baseBlock)
        {
            _scoped = true;
            return base.BeginScope(baseBlock);
        }

        public override IScopedTrieStore GetTrieStore(Hash256? address)
        {
            if (!_scoped) throw new InvalidOperationException("BeginScope has not been called");
            return base.GetTrieStore(address);
        }
    }

    /// <summary>A plain non-disposing wrapper (the shared inner store is owned by the test, not the shadow).</summary>
    private sealed class NonDisposingReadOnlyStore(ITrieStore inner) : DelegatingReadOnlyTrieStore(inner);

    /// <summary>
    /// Pass-through <see cref="IReadOnlyTrieStore"/> that never disposes its shared inner store; tests own the
    /// inner store's lifetime, while the shadow disposes one of these wrappers per computation.
    /// </summary>
    private abstract class DelegatingReadOnlyTrieStore(ITrieStore inner) : IReadOnlyTrieStore
    {
        public bool HasRoot(Hash256 stateRoot) => inner.HasRoot(stateRoot);
        public virtual IDisposable BeginScope(BlockHeader? baseBlock) => inner.BeginScope(baseBlock);
        public virtual IScopedTrieStore GetTrieStore(Hash256? address) => inner.GetTrieStore(address);
        public IBlockCommitter BeginBlockCommit(ulong blockNumber) => inner.BeginBlockCommit(blockNumber);
        public ICommitter BeginCommit(Hash256? address, TrieNode? root, WriteFlags writeFlags) => inner.BeginCommit(address, root, writeFlags);
        public TrieNode FindCachedOrUnknown(Hash256? address, in TreePath path, Hash256 hash) => inner.FindCachedOrUnknown(address, path, hash);
        public byte[]? LoadRlp(Hash256? address, in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) => inner.LoadRlp(address, path, hash, flags);
        public byte[]? TryLoadRlp(Hash256? address, in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) => inner.TryLoadRlp(address, path, hash, flags);
        public INodeStorage.KeyScheme Scheme => inner.Scheme;
        public void Dispose() { } // shared inner store outlives the per-lane wrapper
    }
}
