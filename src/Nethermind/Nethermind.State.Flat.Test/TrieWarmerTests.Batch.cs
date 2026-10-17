// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.ScopeProvider;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

public partial class TrieWarmerTests
{
    [TestCase(8, false, TestName = "QueuedBatch_EightAddresses_CompletesEveryJob")]
    [TestCase(64, false, TestName = "QueuedBatch_FullAddressChunk_CompletesEveryJob")]
    [TestCase(65, false, TestName = "QueuedBatch_AddressTail_CompletesEveryJob")]
    [TestCase(8, true, TestName = "QueuedBatch_EightSlots_CompletesEveryJob")]
    [TestCase(65, true, TestName = "QueuedBatch_SlotTail_CompletesEveryJob")]
    public async Task QueuedBatch_WithCompatibleJobs_CompletesEveryJob(int count, bool storage)
    {
        await using TrieWarmer warmer = new(_logManager, _config);
        using BatchScope context = new();
        object target = storage ? context.Scope.CreateStorageTree(TestItem.AddressA) : context.Scope;
        for (int i = 0; i < count; i++) EnqueueBatchJob(warmer, context.Scope, target, i, 0);
        DrainBatchJobs(warmer);

        Assert.That(context.Scope.OutstandingWarmups, Is.Zero, "every accepted job must complete once, including the scalar tail");
    }

    [Test]
    public async Task QueuedBatch_WithDifferentTargets_DoesNotMixCompletionOwners()
    {
        await using TrieWarmer warmer = new(_logManager, _config);
        using BatchScope first = new();
        using BatchScope second = new();
        for (int i = 0; i < 8; i++) EnqueueBatchJob(warmer, first.Scope, first.Scope, i, 0);
        for (int i = 0; i < 8; i++) EnqueueBatchJob(warmer, second.Scope, second.Scope, i, 0);
        DrainBatchJobs(warmer);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first.Scope.OutstandingWarmups, Is.Zero, "a batch must complete only jobs owned by its exact target");
            Assert.That(second.Scope.OutstandingWarmups, Is.Zero, "the incompatible dequeued job and its remaining queue must still run");
        }
    }

    [Test]
    public async Task QueuedBatch_WithStaleGeneration_SkipsReadsAndCompletesEveryJob()
    {
        await using TrieWarmer warmer = new(_logManager, _config);
        using BatchScope context = new(TestItem.KeccakA);
        for (int i = 0; i < 8; i++) EnqueueBatchJob(warmer, context.Scope, context.Scope, i, 1);
        DrainBatchJobs(warmer);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(context.Scope.OutstandingWarmups, Is.Zero, "stale jobs must release their completion ownership");
            Assert.That(context.Cache.Lookups, Is.Zero, "a stale group must stop before opening its warmup trie");
        }
        context.Reader.DidNotReceive().TryLoadStateRlp(Arg.Any<TreePath>(), Arg.Any<ReadFlags>());
    }

    [Test]
    public async Task QueuedBatch_WithMixedGenerations_DoesNotDiscardCurrentJobs()
    {
        await using TrieWarmer warmer = new(_logManager, _config);
        using BatchScope context = new(TestItem.KeccakA);
        for (int i = 0; i < 8; i++) EnqueueBatchJob(warmer, context.Scope, context.Scope, i, 1);
        for (int i = 8; i < 16; i++) EnqueueBatchJob(warmer, context.Scope, context.Scope, i, 0);
        DrainBatchJobs(warmer);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(context.Scope.OutstandingWarmups, Is.Zero, "both generations must release every completion owner");
            Assert.That(context.Cache.Lookups, Is.GreaterThan(0), "current jobs must still open their trie after the stale group stops");
        }
        context.Reader.Received().TryLoadStateRlp(Arg.Any<TreePath>(), Arg.Any<ReadFlags>());
    }

    [Test]
    public async Task QueuedBatch_WhenReadThrows_CompletesGroupAndHandlesIncompatibleJob()
    {
        await using TrieWarmer warmer = new(_logManager, _config);
        using BatchScope context = new(TestItem.KeccakA);
        context.Reader.TryLoadStateRlp(Arg.Any<TreePath>(), Arg.Any<ReadFlags>()).Returns(_ => throw new InvalidOperationException("read failed"));
        for (int i = 0; i < 8; i++) EnqueueBatchJob(warmer, context.Scope, context.Scope, i, 0);
        ITrieWarmer.IAddressWarmer incompatible = Substitute.For<ITrieWarmer.IAddressWarmer>();
        EnqueueRawBatchJob(warmer, NewBatchJob(incompatible, TestItem.AddressB, default, 7));
        object first = DequeueBatchJob(warmer)!;
        TargetInvocationException? error = Assert.Throws<TargetInvocationException>(() => InvokeBatch(warmer, first));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(error!.InnerException, Is.TypeOf<InvalidOperationException>(), "unexpected native failures must remain visible to the worker error handler");
            Assert.That(context.Scope.OutstandingWarmups, Is.Zero, "a failing batch must complete every compatible job");
            Assert.That(DequeueBatchJob(warmer), Is.Null, "the incompatible dequeued job must not be requeued or lost");
        }
        incompatible.Received(1).WarmUpStateTrie(TestItem.AddressB, 7);
        context.Scope.Dispose();
        context.Reader.Received(1).Dispose();
    }

    [TestCase(8, TestName = "QueuedBatch_ChangedTarget_EightFollowingJobsStayBatched")]
    [TestCase(64, TestName = "QueuedBatch_ChangedTarget_FullFollowingGroupStaysBatched")]
    public async Task QueuedBatch_AfterDifferentTarget_BatchesFollowingGroup(int count)
    {
        await using TrieWarmer warmer = new(_logManager, _config);
        using BatchScope first = new();
        using BatchScope second = new(TestItem.KeccakA);
        for (int i = 0; i < 8; i++) EnqueueBatchJob(warmer, first.Scope, first.Scope, i, 0);
        for (int i = 0; i < count; i++) EnqueueBatchJob(warmer, second.Scope, second.Scope, i, 0);
        DrainBatchJobs(warmer);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first.Scope.OutstandingWarmups, Is.Zero);
            Assert.That(second.Scope.OutstandingWarmups, Is.Zero);
        }
        second.Reader.Received(1).TryLoadStateRlp(Arg.Any<TreePath>(), Arg.Any<ReadFlags>());
    }

    [Test]
    public async Task QueuedBatch_WhenScalarLookaheadThrows_CompletesFollowingJob()
    {
        await using TrieWarmer warmer = new(_logManager, _config);
        using BatchScope first = new(TestItem.KeccakA);
        using BatchScope second = new();
        first.Reader.TryLoadStateRlp(Arg.Any<TreePath>(), Arg.Any<ReadFlags>()).Returns(_ => throw new InvalidOperationException("read failed"));
        EnqueueBatchJob(warmer, first.Scope, first.Scope, 0, 0);
        for (int i = 0; i < 8; i++) EnqueueBatchJob(warmer, second.Scope, second.Scope, i, 0);
        object job = DequeueBatchJob(warmer)!;
        TargetInvocationException? error = Assert.Throws<TargetInvocationException>(() => InvokeBatch(warmer, job));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(error!.InnerException, Is.TypeOf<InvalidOperationException>());
            Assert.That(first.Scope.OutstandingWarmups, Is.Zero);
            Assert.That(second.Scope.OutstandingWarmups, Is.EqualTo(7), "only the owned lookahead job must complete");
        }
        DrainBatchJobs(warmer);
        Assert.That(second.Scope.OutstandingWarmups, Is.Zero);
    }

    [Test]
    public async Task QueuedBatch_WithMissingNodeFailure_DoesNotRetryEveryKey()
    {
        await using TrieWarmer warmer = new(_logManager, _config);
        using BatchScope context = new(TestItem.KeccakA);
        context.Reader.TryLoadStateRlp(Arg.Any<TreePath>(), Arg.Any<ReadFlags>()).Returns(_ => throw new TrieNodeException("node removed", TreePath.Empty, TestItem.KeccakA));
        for (int i = 0; i < 8; i++) EnqueueBatchJob(warmer, context.Scope, context.Scope, i, 0);
        DrainBatchJobs(warmer);

        Assert.That(context.Scope.OutstandingWarmups, Is.Zero);
        context.Reader.Received(1).TryLoadStateRlp(Arg.Any<TreePath>(), Arg.Any<ReadFlags>());
    }

    [Test]
    public async Task QueuedBatch_WithEightActiveWorkers_LeavesWorkForOtherWorkers()
    {
        await using TrieWarmer warmer = new(_logManager, _config);
        using BatchScope context = new();
        for (int i = 0; i < 64; i++) EnqueueBatchJob(warmer, context.Scope, context.Scope, i, 0);
        FieldInfo activeProcessors = typeof(TrieWarmer).GetField("_activeProcessors", BindingFlags.NonPublic | BindingFlags.Instance)!;
        activeProcessors.SetValue(warmer, 8);
        try
        {
            InvokeBatch(warmer, DequeueBatchJob(warmer)!);
            Assert.That(context.Scope.OutstandingWarmups, Is.EqualTo(56), "one worker must not consume the other workers' share");
        }
        finally
        {
            activeProcessors.SetValue(warmer, 0);
        }
        DrainBatchJobs(warmer);
        Assert.That(context.Scope.OutstandingWarmups, Is.Zero);
    }

    [TestCase(7, 0, 8, TestName = "QueuedBatch_Budget_NoScheduledWorkers")]
    [TestCase(63, 1, 64, TestName = "QueuedBatch_Budget_SingleWorker")]
    [TestCase(63, 8, 8, TestName = "QueuedBatch_Budget_EightWorkers")]
    [TestCase(31, 8, 4, TestName = "QueuedBatch_Budget_SmallWorkerShare")]
    [TestCase(1023, 8, 64, TestName = "QueuedBatch_Budget_CapacityBound")]
    public void QueuedBatch_BudgetPreservesWorkerShare(long pending, int workers, int expected) =>
        Assert.That(TrieWarmer.GetWarmUpBatchSize(pending, workers), Is.EqualTo(expected));

    [Test]
    public async Task QueuedBatch_AcrossContracts_ReadsIndependentRootsTogether([Values(8, 16, 64, 65)] int count)
    {
        await using TrieWarmer warmer = new(_logManager, _config);
        CohortReader reader = new(count);
        using BatchScope context = new(reader: reader);
        FlatStorageTree[] trees = new FlatStorageTree[count];
        for (int i = 0; i < count; i++)
        {
            trees[i] = (FlatStorageTree)context.Scope.CreateStorageTree(TestItem.Addresses[i]);
            EnqueueBatchJob(warmer, context.Scope, trees[i], i, 0);
        }
        DrainBatchJobs(warmer);
        int reads = reader.Reads;
        for (int i = 0; i < count; i++) EnqueueBatchJob(warmer, context.Scope, trees[i], i, 0);
        DrainBatchJobs(warmer);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(context.Scope.OutstandingWarmups, Is.Zero);
            Assert.That(reader.MixedBatchSizes, Is.Not.Empty, "different contracts must reach the mixed batch capability");
            Assert.That(reader.MixedBatchSizes[0], Is.EqualTo(Math.Min(count, 64)));
            Assert.That(reader.Reads, Is.EqualTo(reads), "all-cached mixed cohorts must avoid further persistence reads");
        }
        if (count <= 64)
            foreach (FlatStorageTree tree in trees)
                Assert.That(typeof(FlatStorageTree).GetField("_trees", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tree),
                    Is.Null, "mixed warmup must not initialize the execution tree through mutable snapshot readers");
    }

    [TestCase(false, TestName = "QueuedMixedBatch_MissingRoot_StillWarmsOtherContracts")]
    [TestCase(true, TestName = "QueuedMixedBatch_ForeignRoot_StillWarmsOtherContracts")]
    public async Task QueuedBatch_WithUnavailableContract_StillWarmsOtherContracts(bool foreignRoot)
    {
        const int count = 16;
        await using TrieWarmer warmer = new(_logManager, _config);
        CohortReader reader = new(count) { UnavailableAddress = TestItem.Addresses[0].ToAccountPath.ToHash256(), ForeignRoot = foreignRoot };
        using BatchScope context = new(reader: reader);
        FlatStorageTree[] trees = new FlatStorageTree[count];
        for (int i = 0; i < count; i++)
        {
            trees[i] = (FlatStorageTree)context.Scope.CreateStorageTree(TestItem.Addresses[i]);
            EnqueueBatchJob(warmer, context.Scope, trees[i], i, 0);
        }
        DrainBatchJobs(warmer);
        int reads = reader.Reads;
        for (int i = 1; i < count; i++) EnqueueBatchJob(warmer, context.Scope, trees[i], i, 0);
        DrainBatchJobs(warmer);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(context.Scope.OutstandingWarmups, Is.Zero);
            Assert.That(reader.MixedBatchSizes[0], Is.EqualTo(count));
            Assert.That(reader.Reads, Is.EqualTo(reads), "one missing or foreign RLP must not discard valid contracts in the cohort");
        }
        StorageTree? unavailable = typeof(FlatStorageTree).GetField("_cohortWarmupTree", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(trees[0]) as StorageTree;
        Assert.That(unavailable, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(unavailable!.RootRef!.HasRlp, Is.False, "foreign bytes must be rejected before they reach the requested root node");
            Assert.That(unavailable.RootHash, Is.EqualTo(((IPersistence.IPersistenceReader)reader).GetAccount(TestItem.Addresses[0])!.StorageRoot));
        }
    }

    private sealed class CohortReader : NoopPersistenceReader, IPersistence.IPersistenceReader, IBatchTriePersistenceReader
    {
        private readonly Dictionary<Address, Account> _accounts = [];
        private readonly Dictionary<Hash256, byte[]> _rlps = [];
        public List<int> MixedBatchSizes { get; } = [];
        public int Reads;
        public Hash256? UnavailableAddress;
        public bool ForeignRoot;

        public CohortReader(int count)
        {
            for (int i = 0; i < count; i++)
            {
                using MemDb db = new();
                RawScopedTrieStore store = new(db);
                StorageTree tree = new(store, LimboLogs.Instance);
                tree.Set((UInt256)(ulong)i, [(byte)(i + 1)]);
                tree.Commit();
                _accounts[TestItem.Addresses[i]] = new Account(1, 100, tree.RootHash, Keccak.OfAnEmptyString);
                byte[] rlp = store.LoadRlp(TreePath.Empty, tree.RootHash)!;
                Assert.That(rlp, Is.Not.Null.And.Not.Empty, "precondition: the committed root RLP must exist");
                Assert.That(ValueKeccak.Compute(rlp), Is.EqualTo(tree.RootHash.ValueHash256), "precondition: every cohort fixture must provide the committed root RLP");
                _rlps[TestItem.Addresses[i].ToAccountPath.ToHash256()] = rlp;
            }
        }

        Account? IPersistence.IPersistenceReader.GetAccount(Address address) => _accounts.GetValueOrDefault(address);
        byte[]? IPersistence.IPersistenceReader.TryLoadStorageRlp(Hash256 address, in TreePath path, ReadFlags flags) => Read(address);
        public void LoadRlps(ReadOnlySpan<(Hash256? Address, TreePath Path)> requests, Span<byte[]?> values)
        {
            MixedBatchSizes.Add(requests.Length);
            for (int i = 0; i < requests.Length; i++) values[i] = Read(requests[i].Address!);
        }
        public void LoadStateRlps(ReadOnlySpan<TreePath> paths, Span<byte[]?> values) => throw new InvalidOperationException("Unexpected state batch");
        public void LoadStorageRlps(Hash256 address, ReadOnlySpan<TreePath> paths, Span<byte[]?> values)
        {
            for (int i = 0; i < paths.Length; i++) values[i] = Read(address);
        }
        private byte[]? Read(Hash256 address)
        {
            Reads++;
            if (address == UnavailableAddress) return ForeignRoot ? _rlps[TestItem.Addresses[1].ToAccountPath.ToHash256()] : null;
            return _rlps[address];
        }
    }

    private static object NewBatchJob(object target, Address? address, UInt256 slot, int sequence) =>
        Activator.CreateInstance(typeof(TrieWarmer).GetNestedType("Job", BindingFlags.NonPublic)!,
            [target, address, slot, sequence, false])!;

    private static void EnqueueBatchJob(TrieWarmer warmer, FlatWorldStateScope owner, object target, int key, int sequence)
    {
        owner.IncrementOutstandingWarmups();
        EnqueueRawBatchJob(warmer, NewBatchJob(target, target is FlatWorldStateScope ? TestItem.Addresses[key] : null,
            (UInt256)(ulong)key, sequence));
    }

    private static void EnqueueRawBatchJob(TrieWarmer warmer, object job)
    {
        object queue = typeof(TrieWarmer).GetField("_jobBufferMultiThreaded", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(warmer)!;
        bool added = (bool)queue.GetType().GetMethod("TryEnqueue")!.Invoke(queue, [job])!;
        Assert.That(added, Is.True, "precondition: the isolated queue has room for every test job");
    }

    private static object? DequeueBatchJob(TrieWarmer warmer)
    {
        object?[] arguments = [null];
        bool found = (bool)typeof(TrieWarmer).GetMethod("TryDequeue", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(warmer, arguments)!;
        return found ? arguments[0] : null;
    }

    private static void InvokeBatch(TrieWarmer warmer, object first) =>
        typeof(TrieWarmer).GetMethod("HandleJobs", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(warmer, [first]);

    private static void DrainBatchJobs(TrieWarmer warmer)
    {
        while (DequeueBatchJob(warmer) is { } first) InvokeBatch(warmer, first);
    }

    private sealed class BatchScope : IDisposable
    {
        public IPersistence.IPersistenceReader Reader { get; }
        public BatchTrieCache Cache { get; } = new();
        public FlatWorldStateScope Scope { get; }

        public BatchScope(Hash256? root = null, IPersistence.IPersistenceReader? reader = null)
        {
            Reader = reader ?? Substitute.For<IPersistence.IPersistenceReader>();
            FlatDbConfig config = new() { ApplyStorageWritesOnIdleThread = false };
            ResourcePool pool = new(config);
            SnapshotBundle bundle = new(FlatTestHelpers.MakeBundle(pool, Reader), Cache, pool, ResourcePool.Usage.MainBlockProcessing);
            Scope = new(new StateId(0, root ?? Keccak.EmptyTreeHash), bundle,
                Substitute.For<IWorldStateScopeProvider.ICodeDb>(), Substitute.For<IFlatCommitTarget>(),
                config, new NoopTrieWarmer(), LimboLogs.Instance);
        }

        public void Dispose()
        {
            Scope.CompleteWarmups(Scope.OutstandingWarmups);
            Scope.Dispose();
        }
    }

    private sealed class BatchTrieCache : ITrieNodeCache
    {
        public int Lookups;
        public bool TryGet(Hash256? address, in TreePath path, Hash256 hash, [NotNullWhen(true)] out TrieNode? node)
        {
            Lookups++;
            node = null;
            return false;
        }
        public void Add(TransientResource transientResource) { }
        public void Clear() { }
    }
}
