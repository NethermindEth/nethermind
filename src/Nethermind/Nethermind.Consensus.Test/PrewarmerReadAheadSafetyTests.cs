// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Nethermind.State.SnapServer;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Consensus.Test;

/// <summary>
/// Block processing with the in-flight read-ahead forced on every transaction (it starts at the first read past the
/// caches) ends in the state, receipts and gas of executing the block with no prewarming, and leaves only parent-state
/// values in the pre-block caches. Block processing's reads past the caches wait for the read-ahead they started, so it
/// runs its rounds to the end inside the transaction, or, paced at random, races it.
/// </summary>
[TestFixtureSource(nameof(Forks))]
public class PrewarmerReadAheadSafetyTests(IReleaseSpec spec) : PrewarmerReadAheadSafetyScenarios(spec)
{
    internal static readonly IReleaseSpec[] Forks = [Osaka.Instance, Shanghai.Instance];
}

/// <inheritdoc cref="PrewarmerReadAheadSafetyTests"/>
public abstract class PrewarmerReadAheadSafetyScenarios(IReleaseSpec spec) : PrewarmerReadAheadTestBase(spec)
{

    /// <summary>The shapes of the cold tail in one block.</summary>
    private Block ColdTailBlock() => BuildBlock(
        BatchClaim(TestItem.PrivateKeyA, 0, 6, 2_100_000),
        BatchClaim(TestItem.PrivateKeyA, 1, 6, 2_100_000),
        Claim(TestItem.PrivateKeyB, 0, 5, 100),
        Call(TestItem.PrivateKeyC, 0, ColdTailContracts.Deleter, 200_000),
        Call(TestItem.PrivateKeyD, 0, ColdTailContracts.Deleter, 200_000, data: [1]),
        Call(TestItem.PrivateKeyA, 2, Caller),
        Call(TestItem.PrivateKeyB, 1, ColdTailContracts.Chain, 500_000),
        Call(TestItem.PrivateKeyC, 1, ColdTailContracts.Brancher),
        Walk(TestItem.PrivateKeyD, 1, 0x4e4d, 6),
        Call(TestItem.PrivateKeyA, 3, Reverter));

    [Test]
    public void The_caches_hold_only_parent_state_values_after_a_block_processed_with_the_read_ahead([Values] bool random)
    {
        RandomPacing = random;
        Block block = ColdTailBlock();
        Run executed = Process(block, adapter: null);
        // The block's own state exists from here on: a read-ahead that built its scope on it instead of the parent's would
        // put the block's values into the caches.
        block.Header.StateRoot = executed.StateRoot;
        block.Header.Hash = block.Header.CalculateHash();

        List<string> wrong = [];
        int before = PreWarmer.ReadAheadsStarted;

        // A warm pass that got ahead of every read block processing makes would leave it no read past the caches, and so
        // no read-ahead: the prewarmer's reads are held until block processing has started one.
        Run run = ProcessWithSessionAfterReadAhead(block, afterSession: () => wrong.AddRange(EntriesNotAtTheParent()));

        AssertSameRun(run, executed);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(PreWarmer.ReadAheadsStarted - before, Is.GreaterThan(0), "precondition: block processing started a read-ahead");
            Assert.That(WarmingHoldTimeouts, Is.Zero, "precondition: the prewarmer's reads were let go by a read-ahead");
            Assert.That(wrong, Is.Empty, "every account and slot the caches hold before the write-back is the parent's");
            Assert.That(PaceTimeouts, Is.Zero);
        }
    }

    /// <summary>
    /// The 7 Oct 2026 shape: sender A's swap passes the gate on the parent state and debits slot 1, and A's spend runs on
    /// that debit when the two are warmed in order in one scope. The block funds the gate first, so the swap reverts.
    /// Heavy, the sender's transactions are warmed apart on the parent state instead.
    /// </summary>
    [Test]
    public void A_senders_later_transaction_is_checked_against_the_slot_its_earlier_one_did_not_write(
        [Values] bool writesBeforeReverting, [Values] bool heavy, [Values] bool random)
    {
        RandomPacing = random;
        Address swap = writesBeforeReverting ? ColdTailContracts.SwapThenCheck : ColdTailContracts.CheckThenSwap;
        ulong gasLimit = heavy ? 2_100_000ul : 100_000ul;
        Block block = BuildBlock(
            Transfer(TestItem.PrivateKeyB, 0, ColdTailContracts.Gate, 1.Wei),
            Call(TestItem.PrivateKeyA, 0, swap, gasLimit),
            Call(TestItem.PrivateKeyA, 1, swap, gasLimit, data: [2]));

        HandoffWhileWarming(block);

        Assert.That(PaceTimeouts, Is.Zero);
    }

    /// <summary>The incident test as first written: warmed, then handed off; master compares every precondition.</summary>
    [Test]
    public void A_senders_later_transaction_warmed_on_its_earlier_ones_writes_is_rejected([Values] bool writesBeforeReverting)
    {
        Address swap = writesBeforeReverting ? ColdTailContracts.SwapThenCheck : ColdTailContracts.CheckThenSwap;
        (_, int rejected, _) = Handoff(BuildBlock(
            Transfer(TestItem.PrivateKeyB, 0, ColdTailContracts.Gate, 1.Wei),
            Call(TestItem.PrivateKeyA, 0, swap),
            Call(TestItem.PrivateKeyA, 1, swap, data: [2]))).Tally;

        Assert.That(rejected, Is.EqualTo(2));
    }

    // The probes of the 7 Oct fix's verification (e983967450), warmed then handed off, and processed while warming.

    [Test]
    public void P1_A_slot_written_before_the_transactions_is_compared([Values(0x4e4dul, 0ul)] ulong value, [Values] bool whileWarming)
    {
        void Before(IWorldState state)
        {
            state.Set(new StorageCell(Counter, 0), value);
            state.Commit(Spec, commitRoots: false);
        }

        Block block = BuildBlock(
            Call(TestItem.PrivateKeyA, 0, Counter),
            Transfer(TestItem.PrivateKeyB, 0, TestItem.AddressC, 1.Wei),
            Call(TestItem.PrivateKeyD, 0, Logger));
        if (whileWarming) HandoffWhileWarming(block, beforeTransactions: Before);
        else Assert.That(Handoff(block, beforeTransactions: Before).Tally.Rejected, Is.EqualTo(value == 0 ? 0 : 1));
    }

    [Test]
    public void P2_A_slot_a_replay_zeroes_blind_is_compared_by_a_later_first_run([Values] bool whileWarming)
    {
        Block block = BuildBlock(
            Call(TestItem.PrivateKeyA, 0, ColdTailContracts.Zeroer),
            Call(TestItem.PrivateKeyB, 0, ColdTailContracts.Zeroer, data: [1]),
            Transfer(TestItem.PrivateKeyD, 0, TestItem.AddressC, 1.Wei));
        // Since the refresh of invalidated footprints (#14393) the reader's run is refreshed on the cleared slot and taken
        // over, where on 7 Oct it was rejected; either way the block ends in the state of executing it.
        if (whileWarming) HandoffWhileWarming(block);
        else Handoff(block);
    }

    [Test]
    public void P3_A_balance_the_block_changed_is_compared_by_a_first_run([Values] bool whileWarming)
    {
        Block block = BuildBlock(
            Transfer(TestItem.PrivateKeyA, 0, TestItem.AddressC, 1.Wei),
            Transfer(TestItem.PrivateKeyB, 0, TestItem.AddressC, 2.Wei),
            Call(TestItem.PrivateKeyD, 0, BalanceReader));
        if (whileWarming) HandoffWhileWarming(block);
        else Assert.That(Handoff(block).Tally.Rejected, Is.EqualTo(1));
    }

    [Test]
    public void P4_An_account_changed_before_the_transactions_is_compared([Values] bool whileWarming)
    {
        void Before(IWorldState state)
        {
            state.AddToBalance(TestItem.AddressC, 5, Spec, out _);
            state.Commit(Spec, commitRoots: false);
        }

        Block block = BuildBlock(
            Call(TestItem.PrivateKeyD, 0, BalanceReader),
            Transfer(TestItem.PrivateKeyB, 0, TestItem.AddressA, 1.Wei),
            Call(TestItem.PrivateKeyA, 0, Logger));
        if (whileWarming) HandoffWhileWarming(block, beforeTransactions: Before);
        else Assert.That(Handoff(block, beforeTransactions: Before).Tally.Rejected, Is.EqualTo(1));
    }

    [Test]
    public void P5_A_slot_of_a_contract_cleared_earlier_in_the_block_is_compared([Values] bool whileWarming)
    {
        Block block = BuildBlock(
            Call(TestItem.PrivateKeyA, 0, Child),
            Call(TestItem.PrivateKeyB, 0, Factory, gasLimit: 300_000),
            Call(TestItem.PrivateKeyD, 0, Child, data: [1]));
        if (whileWarming) HandoffWhileWarming(block);
        else Assert.That(Handoff(block).Tally.Rejected, Is.GreaterThanOrEqualTo(Spec.IsEip6780Enabled ? 0 : 1));
    }

    /// <summary>
    /// Randomized blocks of the shapes above, from one seeded generator; a failure names its block's seed, which replays the
    /// block and its pacing decisions.
    /// </summary>
    [Test]
    public void Randomized_blocks_end_in_the_state_and_receipts_of_executing_them()
    {
        const int seed = 0x4e4d_2026;
        int blocks = int.TryParse(Environment.GetEnvironmentVariable("NETHERMIND_READAHEAD_CORPUS"), out int count) ? count : 500;
        for (int i = 0; i < blocks; i++)
        {
            int blockSeed = seed + i;
            Random rng = new(blockSeed);
            RandomPacing = rng.Next(2) == 0;
            Block block = RandomColdTailBlock(rng);
            SeedPacing(rng.Next());
            try
            {
                Run executed = Process(block, adapter: null);
                Run run = ProcessWithSession(block);
                AssertSameRun(run, executed);
            }
            catch (Exception)
            {
                TestContext.Out.WriteLine($"Failing block seed {blockSeed} (block {i} of {blocks}, {Spec.Name}, random pacing {RandomPacing})");
                throw;
            }
        }

        TestContext.Out.WriteLine($"{blocks} blocks, {Spec.Name}: read-aheads started {PreWarmer.ReadAheadsStarted}");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(PaceTimeouts, Is.Zero);
            Assert.That(PreWarmer.ReadAheadsStarted, Is.GreaterThan(blocks / 2), "precondition: the read-ahead ran");
        }
    }
}

/// <summary>Without EIP-3607 block processing reads no account of a transaction before matching its footprint.</summary>
[TestFixture]
public class PrewarmerReadAheadWithoutSenderCodeCheckTests() : PrewarmerReadAheadTestBase(WithoutEip3607())
{
    [Test]
    public void A_transaction_ahead_of_its_senders_nonce_is_executed_rather_than_replayed([Values] bool whileWarming)
    {
        Block block = BuildBlock(
            Transfer(TestItem.PrivateKeyA, 0, TestItem.AddressC, 1.Wei),
            Transfer(TestItem.PrivateKeyD, 1, TestItem.AddressB, 1.Wei),
            Transfer(TestItem.PrivateKeyB, 0, TestItem.AddressC, 1.Wei));

        Run run = whileWarming ? HandoffWhileWarming(block) : Handoff(block);

        Assert.That((bool)run.Results[1], Is.False);
    }

    private static IReleaseSpec WithoutEip3607()
    {
        ReleaseSpec spec = ((ReleaseSpec)Osaka.Instance).Clone();
        spec.IsEip3607Enabled = false;
        spec.Name = "Osaka-no3607";
        return spec;
    }
}

/// <summary>
/// A handoff fixture with the read-ahead forced on every transaction, the read-ahead fixture contracts in its genesis,
/// and block processing's reads past the caches paced: they wait for the read-ahead to finish, or at random a moment.
/// </summary>
public abstract class PrewarmerReadAheadTestBase(IReleaseSpec spec) : PrewarmerHandoffTestBase(spec)
{
    private static readonly TimeSpan PaceTimeout = TimeSpan.FromSeconds(10);
    private readonly Lock _pacingLock = new();
    private Random _pacing = new();
    private BlockCachePreWarmer? _paced;
    private int _paceTimeouts;
    private Func<bool>? _warmingHeldUntil;
    private int _warmingHoldTimeouts;

    /// <summary>Whether block processing's reads are paced at random rather than held until the read-ahead is done.</summary>
    protected bool RandomPacing { get; set; }

    protected int PaceTimeouts => Volatile.Read(ref _paceTimeouts);

    /// <summary>How many times the prewarmer's held reads were let go because no read-ahead started in time.</summary>
    protected int WarmingHoldTimeouts => Volatile.Read(ref _warmingHoldTimeouts);

    /// <summary>Seeds the random pacing, so a failure replays its pacing decisions from the seed.</summary>
    protected void SeedPacing(int seed)
    {
        lock (_pacingLock) _pacing = new Random(seed);
    }

    protected override void ConfigureBlocks(BlocksConfig config)
    {
        // A new container: the prewarmer paced on is resolved from it again.
        _paced = null;
        config.PreWarmInFlightReadAhead = true;
        config.PreWarmInFlightReadAheadColdReads = 0;
    }

    protected override void DeployGenesis(IWorldState worldState) => ColdTailContracts.Deploy(worldState, Spec);

    protected override IWorldStateScopeProvider WrapMainScopeProvider(IWorldStateScopeProvider provider) => new PacedScopeProvider(provider, Pace);

    protected override IWorldStateManager WrapWorldStateManager(IWorldStateManager manager) =>
        new WarmingStateManager(manager, provider => new PacedScopeProvider(provider, HoldWarming));

    /// <summary>
    /// Processes the block with its prewarming session open, holding the prewarmer's reads past the caches until block
    /// processing has started a read-ahead, so ordinary warming cannot fill the caches with every read it would start on.
    /// </summary>
    /// <remarks>
    /// Held reads are let go after <see cref="PaceTimeout"/> at the latest, counted in <see cref="WarmingHoldTimeouts"/>:
    /// a block whose read-ahead never starts fails its test rather than hanging it.
    /// </remarks>
    protected Run ProcessWithSessionAfterReadAhead(Block block, Action? afterSession = null)
    {
        BlockCachePreWarmer preWarmer = PreWarmer;
        int before = preWarmer.ReadAheadsStarted;
        Volatile.Write(ref _warmingHeldUntil, () => preWarmer.ReadAheadsStarted != before);
        try
        {
            return ProcessWithSession(block, afterSession);
        }
        finally
        {
            Volatile.Write(ref _warmingHeldUntil, null);
        }
    }

    private void HoldWarming()
    {
        if (Volatile.Read(ref _warmingHeldUntil) is not { } released) return;
        bool letGo = SpinWait.SpinUntil(() => released() || Volatile.Read(ref _warmingHeldUntil) != released, PaceTimeout);
        // Every read goes on from here; only the read that ends the hold counts a timeout.
        if (Interlocked.CompareExchange(ref _warmingHeldUntil, null, released) == released && !letGo)
        {
            Interlocked.Increment(ref _warmingHoldTimeouts);
        }
    }

    private void Pace()
    {
        if (ProcessingScope is null) return;
        if (RandomPacing)
        {
            bool sleep;
            lock (_pacingLock) sleep = _pacing.Next(4) == 0;
            if (sleep) Thread.Sleep(1);
            return;
        }

        BlockCachePreWarmer preWarmer = _paced ??= PreWarmer;
        if (preWarmer.ReadAheadsRunning != 0 && !SpinWait.SpinUntil(() => preWarmer.ReadAheadsRunning == 0, PaceTimeout))
        {
            Interlocked.Increment(ref _paceTimeouts);
        }
    }

    /// <summary>A world state manager whose resettable world states, the ones the prewarmer's envs read, are wrapped.</summary>
    private sealed class WarmingStateManager(IWorldStateManager inner, Func<IWorldStateScopeProvider, IWorldStateScopeProvider> wrap) : IWorldStateManager
    {
        public IWorldStateScopeProvider GlobalWorldState => inner.GlobalWorldState;
        public IStateReader GlobalStateReader => inner.GlobalStateReader;
        public ISnapStateServer SnapStateServer => inner.SnapStateServer;
        public IReadOnlyKeyValueStore? HashServer => inner.HashServer;
        public IWorldStateScopeProvider CreateResettableWorldState() => wrap(inner.CreateResettableWorldState());
        public IWorldStateScopeProvider CreateReadOnlyQueryWorldState() => inner.CreateReadOnlyQueryWorldState();
        public IOverridableWorldScope CreateOverridableWorldScope() => inner.CreateOverridableWorldScope();
        public IReadOnlyTrieStore CreateReadOnlyTrieStore() => inner.CreateReadOnlyTrieStore();
        public bool VerifyTrie(BlockHeader stateAtBlock, CancellationToken cancellationToken) => inner.VerifyTrie(stateAtBlock, cancellationToken);
        public void FlushCache(CancellationToken cancellationToken) => inner.FlushCache(cancellationToken);
        public void DropStateNotReachableFrom(BlockHeader head) => inner.DropStateNotReachableFrom(head);
    }

    /// <summary>Holds the reads of a scope under the pre-block caches, the ones that go to the backing store, at its pace.</summary>
    private sealed class PacedScopeProvider(IWorldStateScopeProvider inner, Action pace) : IWorldStateScopeProvider
    {
        public bool HasRoot(BlockHeader? baseBlock) => inner.HasRoot(baseBlock);

        public bool HasStateForTargetBlock(BlockHeader targetBlock) => inner.HasStateForTargetBlock(targetBlock);

        public bool TryBeginScopeAtTarget(BlockHeader targetBlock, LocalMetrics metrics, [NotNullWhen(true)] out IWorldStateScopeProvider.IScope? scope)
        {
            scope = inner.TryBeginScopeAtTarget(targetBlock, metrics, out IWorldStateScopeProvider.IScope? begun) ? new Scope(begun, pace) : null;
            return scope is not null;
        }

        public bool TryBeginScope(BlockHeader? baseBlock, LocalMetrics metrics, [NotNullWhen(true)] out IWorldStateScopeProvider.IScope? scope)
        {
            scope = inner.TryBeginScope(baseBlock, metrics, out IWorldStateScopeProvider.IScope? begun) ? new Scope(begun, pace) : null;
            return scope is not null;
        }

        private sealed class Scope(IWorldStateScopeProvider.IScope inner, Action pace) : IWorldStateScopeProvider.IScope
        {
            public Hash256 RootHash => inner.RootHash;
            public bool StorageRootsAreAuthoritative => inner.StorageRootsAreAuthoritative;
            public void UpdateRootHash() => inner.UpdateRootHash();
            public void HintWarmAccount(Address address) => inner.HintWarmAccount(address);
            public void HintWarmSlot(Address address, in UInt256 index) => inner.HintWarmSlot(address, in index);

            public Account? Get(Address address)
            {
                pace();
                return inner.Get(address);
            }

            public void HintGet(Address address, Account? account) => inner.HintGet(address, account);
            public IWorldStateScopeProvider.ICodeDb CodeDb => inner.CodeDb;
            public IWorldStateScopeProvider.IStorageTree CreateStorageTree(Address address) => new Tree(inner.CreateStorageTree(address), pace);
            public IWorldStateScopeProvider.IWorldStateWriteBatch StartWriteBatch(int estimatedAccountNum) => inner.StartWriteBatch(estimatedAccountNum);
            public void Commit(ulong blockNumber) => inner.Commit(blockNumber);
            public void WriteBackCommittedState(Func<IWorldStateScopeProvider.IBlockChangeSnapshot> takeSnapshot) => inner.WriteBackCommittedState(takeSnapshot);
            public Task HintBal(ReadOnlyBlockAccessList bal, IWorldStateScopeProvider.IAsyncBalReaderSink? sink = null) => inner.HintBal(bal, sink);
            public void ApplyBal(ReadOnlyBlockAccessList bal, CancellationToken cancellationToken) => inner.ApplyBal(bal, cancellationToken);
            public void Dispose() => inner.Dispose();
        }

        private sealed class Tree(IWorldStateScopeProvider.IStorageTree inner, Action pace) : IWorldStateScopeProvider.IStorageTree
        {
            public Hash256 RootHash => inner.RootHash;

            public void Get(in UInt256 index, out UInt256 value)
            {
                pace();
                inner.Get(in index, out value);
            }

            public void HintSet(in UInt256 index) => inner.HintSet(in index);
            public void HintSet(in UInt256 index, in UInt256 value) => inner.HintSet(in index, in value);
        }
    }
}
