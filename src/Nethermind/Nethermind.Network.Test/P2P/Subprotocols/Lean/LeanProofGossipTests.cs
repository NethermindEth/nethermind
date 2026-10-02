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
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Threading;
using Nethermind.Int256;
using Nethermind.Logging;
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
        public bool VerifyLeanSphincs(in ValueHash256 dataHash, in ValueHash256 verificationKey, ReadOnlySpan<byte> witness) => true;
        public bool VerifyLeanStark(in ValueHash256 dataHash, in ValueHash256 verificationKey, ReadOnlySpan<byte> witness) => true;
        public bool VerifyRecursiveStark(in ValueHash256 depsHash, ReadOnlySpan<byte> aggregatedVk, ReadOnlySpan<byte> proof) => true;
        public byte[] ProveRecursiveStark(in ValueHash256 depsHash, ReadOnlySpan<byte> aggregatedVk, AggregationInput input) => [1];
    }

    [Test]
    public async Task Unchanged_delivered_wrapper_refreshes_after_bounded_receiver_drops()
    {
        FrameDependency dependency = new(Eip8288Constants.LeanSphincsScheme, default, default);
        Transaction transaction = new()
        {
            Type = TxType.FrameTx,
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
