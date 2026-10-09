// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Find;
using Nethermind.Consensus.ProofAggregation;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Threading;
using Nethermind.Crypto;
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
        public Action? OnProving { get; set; }
        public void EnsureAvailable() { }

        public bool VerifyLeanSphincs(in ValueHash256 dataHash, in ValueHash256 verificationKey, ReadOnlySpan<byte> witness) => true;
        public bool VerifyLeanStark(in ValueHash256 dataHash, in ValueHash256 verificationKey, ReadOnlySpan<byte> witness) => true;
        public bool VerifyRecursiveStark(in ValueHash256 depsHash, ReadOnlySpan<byte> aggregatedVk, ReadOnlySpan<byte> proof) => true;
        public byte[] ProveRecursiveStark(in ValueHash256 depsHash, ReadOnlySpan<byte> aggregatedVk, AggregationInput input)
        {
            ProofCalls++;
            OnProving?.Invoke();
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
                Frames = [new(FrameMode.DepVerify, FrameFlags.None, null, Eip8288Constants.LeanSphincsVerificationGas,
                    UInt256.Zero, Eip8288Dependencies.Serialize([dependency]))]
            };
            // Wrapper entries are ordered by the hash a receiver recomputes from the envelope.
            transactions[i].Hash = transactions[i].CalculateHash();
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
    public async Task Cadence_publishes_each_changed_wrapper_once_as_an_announcement()
    {
        using LeanTestNode node = new();
        FakeLink link = new();
        node.Connect(link);
        foreach (int seed in (int[])[1, 2]) node.ProofStore.AddVerified([LeanTestObjects.Dependency(seed)], [[1]], null);
        node.Pending.Add(LeanTestObjects.FrameTransaction(1));
        ManualTimeProvider time = new();
        await using LeanProofGossip gossip = new(node.Wrappers, node.Transport, LimboLogs.Instance, time);
        gossip.Start();
        await time.TimerCreated.WaitAsync(TimeSpan.FromSeconds(5));

        async Task<ValueHash256[]> Announced(int count)
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
            while (link.Sent<AnnounceObjectsMessage>().Length < count)
            {
                time.AdvanceAndFireTimer(TimeSpan.FromSeconds(1));
                await Task.Delay(20, timeout.Token);
            }
            for (int i = 0; i < 3; i++)
            {
                time.AdvanceAndFireTimer(TimeSpan.FromSeconds(1));
                await Task.Delay(20);
            }
            return [.. link.Sent<AnnounceObjectsMessage>().SelectMany(m => m.Descriptors).Select(d => d.ObjectId)];
        }

        ValueHash256[] first = await Announced(1);
        Assert.That(first, Has.Length.EqualTo(1), "an unchanged wrapper is announced once and never re-sent");
        node.Pending.Add(LeanTestObjects.FrameTransaction(2));
        ValueHash256[] second = await Announced(2);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(second, Has.Length.EqualTo(2));
            Assert.That(second.Distinct().Count(), Is.EqualTo(2));
            Assert.That(link.Chunks, Is.Empty, "bodies move only in requested chunks");
            Assert.That(node.Transport.StoredObjects, Is.EqualTo(2));
        }
    }

    private static ProofWrapperService CreateService(Transaction[] transactions, LeanProofStore store, ILeanProofVerifier verifier, ITxPool? pool = null)
    {
        pool ??= Substitute.For<ITxPool>();
        pool.GetPendingTransactions().Returns(transactions);
        ConfigureMembership(pool);
        pool.GetPendingLightBlobTransactionsBySender().Returns(new Dictionary<AddressAsKey, Transaction[]>());
        IBlockFinder finder = Substitute.For<IBlockFinder>();
        finder.Head.Returns(Build.A.Block.TestObject);
        return new ProofWrapperService(pool, new TestSingleReleaseSpecProvider(Eip8288Prototype.Instance), finder, store, verifier);
    }

    private static void ConfigureMembership(ITxPool pool)
        => pool.TryGetPendingTransaction(Arg.Any<ValueHash256>(), out Arg.Any<Transaction?>()).Returns(call =>
        {
            Transaction? found = Array.Find(pool.GetPendingTransactions(), transaction => transaction.Hash!.ValueHash256 == call.Arg<ValueHash256>());
            call[1] = found;
            return found is not null;
        });
}
