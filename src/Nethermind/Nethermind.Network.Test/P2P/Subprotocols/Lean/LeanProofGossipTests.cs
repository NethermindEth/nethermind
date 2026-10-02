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
    public void Rotating_seventeen_distinct_dependencies_reuses_each_proven_selection()
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
        for (int cadence = 0; cadence < 3 * transactions.Length; cadence++)
        {
            Result<byte[]> result = service.BuildWrapper();
            Assert.That(result.IsSuccess, Is.True, result.Error);
            uniqueWrappers.Add(ValueKeccak.Compute(result.Data!));
            RlpReader reader = new(result.Data!);
            MempoolWrapper wrapper = MempoolWrapperDecoder.Instance.Decode(ref reader);
            Assert.That(wrapper.Deps, Has.Count.EqualTo(Eip8288Constants.MaxLeanSigDepsPerWrapper));
            propagated.UnionWith(wrapper.Deps);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(propagated, Has.Count.EqualTo(17));
            Assert.That(uniqueWrappers, Has.Count.EqualTo(17));
            Assert.That(verifier.ProofCalls, Is.EqualTo(17), "each bounded selection is proved once across three rotations");
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
        Func<byte[], bool> removed = _ =>
        {
            Interlocked.Increment(ref removedSends);
            removedFirst.TrySetResult();
            return true;
        };
        gossip.AddPeer(removed);
        gossip.AddPeer(_ =>
        {
            if (Interlocked.Increment(ref remainingSends) == 1) remainingFirst.TrySetResult();
            else remainingRefresh.TrySetResult();
            return true;
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
        gossip.AddPeer(_ =>
        {
            if (Interlocked.Increment(ref sends) == 1) first.TrySetResult();
            else refreshed.TrySetResult();
            return true;
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
