// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Find;
using Nethermind.Consensus.Eip8288;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Threading;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Network.P2P.Subprotocols.Lean;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Lean;

public class LeanProofGossipTests
{
    private sealed class Verifier : ILeanProofVerifier
    {
        public int ProofCalls { get; private set; }
        public void EnsureAvailable() { }

        public bool VerifyLeanSphincs(in ValueHash256 dataHash, in ValueHash256 verificationKey, ReadOnlySpan<byte> witness) => true;
        public bool VerifyLeanStark(in ValueHash256 dataHash, in ValueHash256 verificationKey, ReadOnlySpan<byte> witness) => true;
        public bool VerifyRecursiveStark(in ValueHash256 depsHash, ReadOnlySpan<byte> aggregatedVk, ReadOnlySpan<byte> proof) => true;
        public byte[] ProveRecursiveStark(in ValueHash256 depsHash, ReadOnlySpan<byte> aggregatedVk, AggregationInput input)
        {
            ProofCalls++;
            return [1];
        }
    }

    [Test]
    public void Disjoint_rotation_reuses_proven_groups_without_sliding_overlap()
    {
        LeanProofStore store = new();
        Transaction[] transactions = new Transaction[17];
        for (int i = 0; i < transactions.Length; i++)
        {
            FrameDependency dependency = new(Eip8288Constants.LeanSphincsScheme, ValueKeccak.Compute($"dependency:{i}"), default);
            store.AddVerified([dependency], [[1]], null);
            transactions[i] = new Transaction
            {
                Type = TxType.FrameTx,
                NonceKeys = [UInt256.Zero],
                ChainId = 1,
                SenderAddress = Address.Zero,
                Hash = new Hash256(ValueKeccak.Compute($"transaction:{i}")),
                Frames = [new(FrameMode.DepVerify, FrameFlags.None, null, Eip8288Constants.LeanSphincsVerificationGas,
                    UInt256.Zero, Eip8288Dependencies.Serialize([dependency]))]
            };
        }
        Verifier verifier = new();
        ProofWrapperService service = CreateService(transactions, store, verifier);
        HashSet<ValueHash256> uniqueWrappers = [];
        HashSet<FrameDependency> propagated = [];
        int proofCallsAfterBothGroups = 0;
        for (int cadence = 0; cadence < 3 * transactions.Length; cadence++)
        {
            Result<byte[]> result = service.BuildWrapper();
            Assert.That(result.IsSuccess, Is.True, result.Error);
            uniqueWrappers.Add(ValueKeccak.Compute(result.Data!));
            RlpReader reader = new(result.Data!);
            MempoolWrapper wrapper = MempoolWrapperDecoder.Instance.Decode(ref reader);
            Assert.That(wrapper.Deps.Count, Is.EqualTo(cadence % 2 == 0 ? Eip8288Constants.MaxLeanSigDepsPerWrapper : 1));
            propagated.UnionWith(wrapper.Deps);
            if (cadence == 1) proofCallsAfterBothGroups = verifier.ProofCalls;
            else if (cadence > 1)
                Assert.That(verifier.ProofCalls, Is.EqualTo(proofCallsAfterBothGroups), $"rotation {cadence} reuses already proven groups");
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(propagated, Has.Count.EqualTo(17));
            Assert.That(uniqueWrappers, Has.Count.EqualTo(2));
            Assert.That(proofCallsAfterBothGroups, Is.Positive, "both groups were actually proven before checking reuse");
        }
    }

    [Test]
    public async Task Removed_peer_receives_no_refresh_while_remaining_peer_does()
    {
        FrameDependency dependency = new(Eip8288Constants.LeanSphincsScheme, default, default);
        Transaction transaction = new()
        {
            Type = TxType.FrameTx,
            NonceKeys = [UInt256.Zero],
            ChainId = 1,
            SenderAddress = Address.Zero,
            Hash = TestItem.KeccakA,
            Frames = [new(FrameMode.DepVerify, FrameFlags.None, null, Eip8288Constants.LeanSphincsVerificationGas,
                UInt256.Zero, Eip8288Dependencies.Serialize([dependency]))]
        };
        LeanProofStore store = new();
        store.AddVerified([dependency], [[1]], null);
        ManualTimeProvider time = new();
        await using LeanProofGossip gossip = new(CreateService([transaction], store, new Verifier()), LimboLogs.Instance, time);
        TaskCompletionSource removedFirst = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource remainingFirst = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource remainingRefresh = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int removedSends = 0;
        int remainingSends = 0;
        Func<ReadOnlyMemory<byte>, ValueHash256, CancellationToken, ValueTask<bool>> removed = (_, _, _) =>
        {
            Interlocked.Increment(ref removedSends);
            removedFirst.TrySetResult();
            return new(true);
        };
        gossip.AddPeer(removed);
        gossip.AddPeer((_, _, _) =>
        {
            if (Interlocked.Increment(ref remainingSends) == 1) remainingFirst.TrySetResult();
            else remainingRefresh.TrySetResult();
            return new(true);
        });
        await time.TimerCreated.WaitAsync(TimeSpan.FromSeconds(5));
        time.AdvanceAndFireTimer(TimeSpan.FromSeconds(1));
        await Task.WhenAll(removedFirst.Task, remainingFirst.Task).WaitAsync(TimeSpan.FromSeconds(5));
        gossip.RemovePeer(removed);
        time.AdvanceAndFireTimer(TimeSpan.FromSeconds(LeanProofGossip.RefreshIntervalSeconds)
            + TimeSpan.FromMilliseconds(LeanProofGossip.RefreshJitterMilliseconds));
        await remainingRefresh.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(removedSends, Is.EqualTo(1));
        Assert.That(remainingSends, Is.EqualTo(2));
    }

    [Test]
    public async Task Async_peer_memos_only_completed_transfer_and_does_not_restart_active_object()
    {
        FrameDependency dependency = new(Eip8288Constants.LeanSphincsScheme, default, default);
        Transaction transaction = new()
        {
            Type = TxType.FrameTx,
            NonceKeys = [UInt256.Zero],
            ChainId = 1,
            SenderAddress = Address.Zero,
            Hash = TestItem.KeccakA,
            Frames = [new(FrameMode.DepVerify, FrameFlags.None, null, Eip8288Constants.LeanSphincsVerificationGas,
                UInt256.Zero, Eip8288Dependencies.Serialize([dependency]))]
        };
        LeanProofStore store = new();
        store.AddVerified([dependency], [[1]], null);
        ManualTimeProvider time = new();
        await using LeanProofGossip gossip = new(CreateService([transaction], store, new Verifier()), LimboLogs.Instance, time);
        TaskCompletionSource first = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource second = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int sends = 0;
        gossip.AddPeer(async (_, _, token) =>
        {
            if (Interlocked.Increment(ref sends) == 1)
            {
                first.SetResult();
                await completed.Task.WaitAsync(token);
                return false; // An incomplete transfer must remain eligible for retry.
            }
            second.SetResult();
            return true;
        });
        await time.TimerCreated.WaitAsync(TimeSpan.FromSeconds(5));
        time.AdvanceAndFireTimer(TimeSpan.FromSeconds(1));
        await first.Task.WaitAsync(TimeSpan.FromSeconds(5));
        time.AdvanceAndFireTimer(TimeSpan.FromSeconds(10));
        Assert.That(sends, Is.EqualTo(1), "cadence does not cancel or restart an active object");
        completed.SetResult();
        for (int attempt = 0; attempt < 20 && !second.Task.IsCompleted; attempt++)
        {
            await Task.Delay(10);
            time.AdvanceAndFireTimer(TimeSpan.FromSeconds(1));
        }
        await second.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(sends, Is.EqualTo(2));
        time.AdvanceAndFireTimer(TimeSpan.FromSeconds(1));
        await Task.Delay(20);
        Assert.That(sends, Is.EqualTo(2), "successful completion is memoized");
    }

    [Test]
    public async Task Peers_and_late_joiner_share_the_wrapper_and_its_commitment()
    {
        ManualTimeProvider time = new();
        await using LeanProofGossip gossip = new(CreateService(), LimboLogs.Instance, time);
        TaskCompletionSource<(ReadOnlyMemory<byte> Bytes, ValueHash256 Hash)>[] deliveries = new TaskCompletionSource<(ReadOnlyMemory<byte>, ValueHash256)>[8];
        for (int index = 0; index < deliveries.Length; index++)
        {
            TaskCompletionSource<(ReadOnlyMemory<byte>, ValueHash256)> delivered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            deliveries[index] = delivered;
            gossip.AddPeer((bytes, hash, _) => { delivered.TrySetResult((bytes, hash)); return new(true); });
        }
        await time.TimerCreated.WaitAsync(TimeSpan.FromSeconds(5));
        time.AdvanceAndFireTimer(TimeSpan.FromSeconds(1));
        (ReadOnlyMemory<byte> Bytes, ValueHash256 Hash)[] results = await Task.WhenAll(Array.ConvertAll(deliveries, delivered => delivered.Task)).WaitAsync(TimeSpan.FromSeconds(5));
        TaskCompletionSource<(ReadOnlyMemory<byte> Bytes, ValueHash256 Hash)> late = new(TaskCreationOptions.RunContinuationsAsynchronously);
        gossip.AddPeer((bytes, hash, _) => { late.TrySetResult((bytes, hash)); return new(true); });
        (ReadOnlyMemory<byte> Bytes, ValueHash256 Hash) joined = await late.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[0].Hash, Is.EqualTo(ValueKeccak.Compute(results[0].Bytes.Span)));
            foreach ((ReadOnlyMemory<byte> bytes, ValueHash256 hash) in results)
            {
                Assert.That(bytes.Equals(results[0].Bytes), Is.True, "peers share one readonly backing buffer");
                Assert.That(hash, Is.EqualTo(results[0].Hash));
            }
            Assert.That(joined.Bytes.Equals(results[0].Bytes), Is.True);
            Assert.That(joined.Hash, Is.EqualTo(results[0].Hash));
        }
    }

    [Test]
    public async Task Synchronous_peer_work_does_not_hold_the_registry_lock_or_block_other_peers()
    {
        ManualTimeProvider time = new();
        await using LeanProofGossip gossip = new(CreateService(), LimboLogs.Instance, time);
        using ManualResetEventSlim release = new();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource other = new(TaskCreationOptions.RunContinuationsAsynchronously);
        gossip.AddPeer((_, _, _) =>
        {
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Peer release timed out");
            return new(true);
        });
        gossip.AddPeer((_, _, _) => { other.TrySetResult(); return new(true); });
        await time.TimerCreated.WaitAsync(TimeSpan.FromSeconds(5));
        time.AdvanceAndFireTimer(TimeSpan.FromSeconds(1));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await other.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Run(() => gossip.AddPeer((_, _, _) => new(true))).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { release.Set(); }
    }

    [Test]
    public async Task Disposal_waits_for_a_removed_peers_active_worker()
    {
        ManualTimeProvider time = new();
        await using LeanProofGossip gossip = new(CreateService(), LimboLogs.Instance, time);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<ReadOnlyMemory<byte>, ValueHash256, CancellationToken, ValueTask<bool>> send = async (_, _, _) =>
        {
            entered.TrySetResult();
            await release.Task;
            return true;
        };
        gossip.AddPeer(send);
        await time.TimerCreated.WaitAsync(TimeSpan.FromSeconds(5));
        time.AdvanceAndFireTimer(TimeSpan.FromSeconds(1));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        gossip.RemovePeer(send);
        Task disposal = gossip.DisposeAsync().AsTask();
        try
        {
            await Task.WhenAny(disposal, Task.Delay(100));
            Assert.That(disposal.IsCompleted, Is.False, "removed workers still own the transfer until it finishes");
        }
        finally { release.TrySetResult(); }
        await disposal.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static ProofWrapperService CreateService()
    {
        FrameDependency dependency = new(Eip8288Constants.LeanSphincsScheme, default, default);
        Transaction transaction = new()
        {
            Type = TxType.FrameTx,
            NonceKeys = [UInt256.Zero],
            ChainId = 1,
            SenderAddress = Address.Zero,
            Hash = TestItem.KeccakA,
            Frames = [new(FrameMode.DepVerify, FrameFlags.None, null, Eip8288Constants.LeanSphincsVerificationGas,
                UInt256.Zero, Eip8288Dependencies.Serialize([dependency]))]
        };
        LeanProofStore store = new();
        store.AddVerified([dependency], [[1]], null);
        return CreateService([transaction], store, new Verifier());
    }

    private static ProofWrapperService CreateService(Transaction[] transactions, LeanProofStore store, ILeanProofVerifier verifier)
    {
        ITxPool pool = Substitute.For<ITxPool>();
        pool.GetPendingTransactions().Returns(transactions);
        pool.GetPendingLightBlobTransactionsBySender().Returns(new Dictionary<AddressAsKey, Transaction[]>());
        IBlockFinder finder = Substitute.For<IBlockFinder>();
        finder.Head.Returns(Build.A.Block.TestObject);
        return new ProofWrapperService(pool, new TestSingleReleaseSpecProvider(Eip8288Prototype.Instance), finder, store, verifier);
    }

    [Test]
    public async Task Unchanged_delivered_wrapper_refreshes_after_bounded_receiver_drops()
    {
        FrameDependency dependency = new(Eip8288Constants.LeanSphincsScheme, default, default);
        Transaction transaction = new()
        {
            Type = TxType.FrameTx,
            NonceKeys = [UInt256.Zero],
            ChainId = 1,
            SenderAddress = Address.Zero,
            Hash = TestItem.KeccakA,
            Frames = [new(FrameMode.DepVerify, FrameFlags.None, null, Eip8288Constants.LeanSphincsVerificationGas,
                UInt256.Zero, Eip8288Dependencies.Serialize([dependency]))]
        };
        ITxPool pool = Substitute.For<ITxPool>();
        pool.GetPendingTransactions().Returns([transaction]);
        pool.GetPendingLightBlobTransactionsBySender().Returns(new Dictionary<AddressAsKey, Transaction[]>());
        IBlockFinder finder = Substitute.For<IBlockFinder>();
        finder.Head.Returns(Build.A.Block.TestObject);
        LeanProofStore store = new();
        store.AddVerified([dependency], [[1]], null);
        ProofWrapperService wrappers = new(pool, new TestSingleReleaseSpecProvider(Eip8288Prototype.Instance), finder, store, new Verifier());
        ManualTimeProvider time = new();
        await using LeanProofGossip gossip = new(wrappers, LimboLogs.Instance, time);
        TaskCompletionSource first = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource refreshed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int sends = 0;
        gossip.AddPeer((_, _, _) =>
        {
            if (Interlocked.Increment(ref sends) == 1) first.TrySetResult();
            else refreshed.TrySetResult();
            return new(true);
        });
        await time.TimerCreated.WaitAsync(TimeSpan.FromSeconds(5));
        time.AdvanceAndFireTimer(TimeSpan.FromSeconds(1));
        await first.Task.WaitAsync(TimeSpan.FromSeconds(5));
        time.AdvanceAndFireTimer(TimeSpan.FromSeconds(LeanProofGossip.RefreshIntervalSeconds)
            + TimeSpan.FromMilliseconds(LeanProofGossip.RefreshJitterMilliseconds));
        await refreshed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(sends, Is.EqualTo(2));
    }
}
