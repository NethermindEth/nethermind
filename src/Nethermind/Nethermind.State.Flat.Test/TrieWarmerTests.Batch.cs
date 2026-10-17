// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
        public IPersistence.IPersistenceReader Reader { get; } = Substitute.For<IPersistence.IPersistenceReader>();
        public BatchTrieCache Cache { get; } = new();
        public FlatWorldStateScope Scope { get; }

        public BatchScope(Hash256? root = null)
        {
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
